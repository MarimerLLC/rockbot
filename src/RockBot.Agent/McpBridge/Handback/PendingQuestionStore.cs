using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using RockBot.Tools.Mcp;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge.Handback;

/// <summary>
/// The bridge's open hand-back questions: caps, the per-question TTL, and the write-ahead to the
/// <see cref="PendingQuestionLedger"/>. Every transition a question can make goes through here,
/// so the in-memory state and the ledger agree.
/// </summary>
internal sealed class PendingQuestionStore : IDisposable
{
    /// <summary>Longest tool description kept in a ledger entry.</summary>
    public const int ToolDescriptionMaxChars = 300;

    /// <summary>Longest user excerpt kept in a ledger entry.</summary>
    public const int UserExcerptMaxChars = 500;

    /// <summary>Finds the last user message in a session before a call started, for the ledger.</summary>
    public delegate Task<PendingTrigger?> TriggerLookup(string sessionId, DateTimeOffset before, CancellationToken ct);

    private readonly PendingQuestionLedger _ledger;
    private readonly int _maxPerSession;
    private readonly int _maxTotal;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TriggerLookup? _trigger;
    private readonly ConcurrentDictionary<string, PendingQuestion> _open = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ITimer> _timers = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PendingQuestionStore(
        PendingQuestionLedger ledger,
        int maxPerSession,
        int maxTotal,
        ILogger logger,
        TimeProvider? time = null,
        TriggerLookup? trigger = null)
    {
        _ledger = ledger;
        _maxPerSession = maxPerSession;
        _maxTotal = maxTotal;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _trigger = trigger;
    }

    public PendingQuestionLedger Ledger => _ledger;

    /// <summary>How many questions are open, which is how many calls are parked.</summary>
    public int OpenCount => _open.Count;

    /// <summary>The open question with this id, or null.</summary>
    public PendingQuestion? Get(string questionId) => _open.GetValueOrDefault(questionId);

    /// <summary>
    /// Opens a question for <paramref name="call"/>: checks the caps, writes the ledger entry, and
    /// starts the TTL. Returns the question, or why it can't be handed back (the caller declines
    /// it in-band).
    /// </summary>
    public async Task<(PendingQuestion? Question, string? Refusal)> OpenAsync(
        HandbackCall call, McpHandbackQuestion question, CancellationToken ct)
    {
        var info = call.Info;
        var now = _time.GetUtcNow();
        var pending = new PendingQuestion
        {
            QuestionId = NewQuestionId(),
            SessionId = info.SessionId,
            ServerName = info.ServerName,
            ToolName = info.ToolName,
            Request = question.Request,
            Round = Math.Max(1, question.Round),
            CreatedAt = now,
            ExpiresAt = now + info.Ttl,
            Call = call,
        };

        lock (_gate)
        {
            if (_open.Count >= _maxTotal)
            {
                McpHandbackDiagnostics.Rejected("bridge-cap");
                return (null, $"too many questions are already waiting for answers (limit {_maxTotal})");
            }

            if (_open.Values.Count(p => p.SessionId == info.SessionId) >= _maxPerSession)
            {
                McpHandbackDiagnostics.Rejected("session-cap");
                return (null, $"this conversation already has {_maxPerSession} unanswered questions from MCP servers");
            }

            _open[pending.QuestionId] = pending;
        }

        try
        {
            var trigger = await LookUpTriggerAsync(info, ct).ConfigureAwait(false);
            await _ledger.AddAsync(BuildEntry(pending, call, trigger), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _open.TryRemove(pending.QuestionId, out _);
            _logger.LogError(ex, "Could not record MCP question from {Server}/{Tool} in the pending ledger; declining it",
                info.ServerName, info.ToolName);
            McpHandbackDiagnostics.Rejected("ledger");
            return (null, "the client could not record the question, so it was not handed back");
        }
        catch
        {
            _open.TryRemove(pending.QuestionId, out _);
            throw;
        }

        _timers[pending.QuestionId] = _time.CreateTimer(
            _ => _ = ExpireAsync(pending.QuestionId), null, info.Ttl, Timeout.InfiniteTimeSpan);

        McpHandbackDiagnostics.HandedBack(info.ServerName);
        _logger.LogInformation(
            "MCP {Server}/{Tool} question {QuestionId} handed back to session {Session} (round {Round})",
            info.ServerName, info.ToolName, pending.QuestionId, info.SessionId, pending.Round);
        return (pending, null);
    }

    /// <summary>
    /// Settles <paramref name="question"/> with the agent's answer. False when it was settled
    /// meanwhile (answered, expired or abandoned): a question is answered once.
    /// </summary>
    public async Task<bool> TryAnswerAsync(PendingQuestion question, McpAnswerSubmission submission, CancellationToken ct)
    {
        if (!_open.TryRemove(question.QuestionId, out _))
            return false;

        StopTimer(question.QuestionId);
        if (!question.TrySubmit(submission))
            return false;

        var declined = !string.Equals(submission.Outcome.Action, McpElicitationActions.Accept, StringComparison.OrdinalIgnoreCase);
        if (declined)
            McpHandbackDiagnostics.DeclinedByAgent(question.ServerName);
        else
            McpHandbackDiagnostics.Answered(question.ServerName);

        await SettleQuietlyAsync(question.QuestionId,
            declined ? PendingQuestionStatus.Declined : PendingQuestionStatus.Answered, null, ct).ConfigureAwait(false);

        _logger.LogInformation("MCP question {QuestionId} for {Server}/{Tool} {Outcome} by the agent",
            question.QuestionId, question.ServerName, question.ToolName, declined ? "declined" : "answered");
        return true;
    }

    /// <summary>
    /// Abandons every open question from <paramref name="serverName"/> and cancels their calls,
    /// because the server was removed or its policy changed.
    /// </summary>
    public Task CancelServerAsync(string serverName, string reason) =>
        CancelWhereAsync(q => string.Equals(q.ServerName, serverName, StringComparison.OrdinalIgnoreCase), reason);

    /// <summary>
    /// Abandons every open question asked of <paramref name="sessionId"/> and cancels their calls,
    /// because the run that held the session has ended (a subagent finished).
    /// </summary>
    public Task CancelSessionAsync(string sessionId, string reason) =>
        CancelWhereAsync(q => string.Equals(q.SessionId, sessionId, StringComparison.Ordinal), reason);

    private async Task CancelWhereAsync(Func<PendingQuestion, bool> match, string reason)
    {
        foreach (var question in _open.Values.Where(match).ToList())
        {
            if (!Abandon(question, reason))
                continue;

            await SettleQuietlyAsync(question.QuestionId, PendingQuestionStatus.Cancelled, reason, CancellationToken.None)
                .ConfigureAwait(false);
            _logger.LogInformation("MCP question {QuestionId} for {Server}/{Tool} cancelled: {Reason}",
                question.QuestionId, question.ServerName, question.ToolName, reason);
        }
    }

    /// <summary>
    /// Abandons every open question because the bridge is stopping. Their ledger entries stay
    /// pending, so the next start finds them and announces the interruption.
    /// </summary>
    public void AbandonAllForShutdown()
    {
        foreach (var question in _open.Values.ToList())
            Abandon(question, "the agent is restarting");
    }

    private bool Abandon(PendingQuestion question, string reason)
    {
        if (!_open.TryRemove(question.QuestionId, out _))
            return false;

        StopTimer(question.QuestionId);

        // Cancel the call before releasing the server's handler, so the handler's return can't
        // send the server a retry nobody will wait for.
        question.Call.Abort(reason);
        question.TrySubmit(new McpAnswerSubmission(McpHandbackOutcome.Cancel(reason), null));
        return true;
    }

    private async Task ExpireAsync(string questionId)
    {
        if (_open.GetValueOrDefault(questionId) is not { } question)
            return;

        var reason = $"the question was not answered within {(int)question.Call.Info.Ttl.TotalMinutes} minutes";
        if (!Abandon(question, reason))
            return;

        McpHandbackDiagnostics.Expired(question.ServerName);
        await SettleQuietlyAsync(questionId, PendingQuestionStatus.Expired, reason, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("MCP question {QuestionId} for {Server}/{Tool} expired; its call was cancelled",
            questionId, question.ServerName, question.ToolName);
    }

    private void StopTimer(string questionId)
    {
        if (_timers.TryRemove(questionId, out var timer))
            timer.Dispose();
    }

    private async Task SettleQuietlyAsync(string questionId, string status, string? reason, CancellationToken ct)
    {
        try
        {
            await _ledger.SettleAsync(questionId, status, reason, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The in-memory outcome stands. At worst a restart announces a question that had
            // already settled, which its notice and the late-answer reply both cover.
            _logger.LogWarning(ex, "Could not record MCP question {QuestionId} as {Status} in the pending ledger",
                questionId, status);
        }
    }

    private async Task<PendingTrigger?> LookUpTriggerAsync(HandbackCallInfo info, CancellationToken ct)
    {
        if (_trigger is null || string.IsNullOrEmpty(info.SessionId))
            return null;

        try
        {
            return await _trigger(info.SessionId, info.StartedAt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read session {Session}'s conversation for a hand-back's context", info.SessionId);
            return null;
        }
    }

    private static PendingQuestionEntry BuildEntry(PendingQuestion pending, HandbackCall call, PendingTrigger? trigger)
    {
        var info = call.Info;
        var schema = pending.Request.RequestedSchema;
        var required = schema?.Required is { Count: > 0 } r ? new HashSet<string>(r, StringComparer.Ordinal) : null;

        return new PendingQuestionEntry
        {
            QuestionId = pending.QuestionId,
            Status = PendingQuestionStatus.Pending,
            SessionId = pending.SessionId,
            CreatedAt = pending.CreatedAt,
            ExpiresAt = pending.ExpiresAt,
            Call = new PendingCallContext
            {
                Server = info.ServerName,
                Tool = info.ToolName,
                ToolDescription = Truncate(Flatten(info.ToolDescription), ToolDescriptionMaxChars),
                Arguments = LlmElicitationResponder.RedactArguments(info.Arguments),
                StartedAt = info.StartedAt,
                Round = pending.Round,
                EarlierRounds = [.. call.EarlierRounds],
            },
            TriggeredBy = trigger,
            Question = new PendingQuestionText
            {
                Message = Flatten(pending.Request.Message) ?? "",
                Fields = [.. (schema?.Properties ?? new Dictionary<string, ModelContextProtocol.Protocol.ElicitRequestParams.PrimitiveSchemaDefinition>())
                    .Select(p => new PendingQuestionField
                    {
                        Name = McpElicitationSchemaDescriber.Flatten(p.Key),
                        Type = McpElicitationSchemaDescriber.Flatten(McpElicitationSchemaDescriber.DescribeType(p.Value)),
                        Required = required?.Contains(p.Key) == true,
                    })],
            },
        };
    }

    /// <summary>
    /// The user's message, scrubbed of secret-shaped text and capped, as a ledger entry keeps it.
    /// </summary>
    public static string ExcerptOf(string? userMessage) =>
        Truncate(McpSecretScrubber.Scrub(Flatten(userMessage)), UserExcerptMaxChars) ?? "";

    private static string? Flatten(string? text) =>
        text is null ? null : McpElicitationSchemaDescriber.Flatten(text);

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string NewQuestionId() => "q_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public void Dispose()
    {
        foreach (var timer in _timers.Values)
            timer.Dispose();
        _timers.Clear();
    }
}
