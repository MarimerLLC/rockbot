using System.Threading.Channels;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge.Handback;

/// <summary>What the bridge knows about a call that can take a hand-back, for the ledger and its texts.</summary>
/// <param name="ServerName">Server called.</param>
/// <param name="ToolName">Tool called.</param>
/// <param name="ToolDescription">The tool's description from <c>tools/list</c>, if known.</param>
/// <param name="Arguments">Raw JSON arguments the agent sent.</param>
/// <param name="SessionId">Session that made the call.</param>
/// <param name="StartedAt">When the call started.</param>
/// <param name="Ttl">How long each of its questions stays open.</param>
internal sealed record HandbackCallInfo(
    string ServerName,
    string ToolName,
    string? ToolDescription,
    string? Arguments,
    string? SessionId,
    DateTimeOffset StartedAt,
    TimeSpan Ttl);

/// <summary>
/// One tool call that can take a hand-back. It is the call's <see cref="IMcpHandbackChannel"/>:
/// a question from the server becomes a <see cref="PendingQuestion"/>, the invoke path hears of it
/// through <see cref="NextAsync"/>, and the server's handler waits until <c>mcp_answer</c> settles
/// it. It also owns the call's cancellation, so the tool-call timeout can be suspended while a
/// question waits and the call can be abandoned.
/// </summary>
internal sealed class HandbackCall : IMcpHandbackChannel, IDisposable
{
    private readonly PendingQuestionStore _store;
    private readonly int _timeoutMs;
    private readonly Channel<PendingQuestion> _questions = Channel.CreateUnbounded<PendingQuestion>();
    private readonly List<PendingEarlierRound> _earlierRounds = [];
    private string? _abortReason;
    private int _handedBack;

    public HandbackCall(PendingQuestionStore store, HandbackCallInfo info, int timeoutMs, CancellationToken lifetime)
    {
        _store = store;
        _timeoutMs = timeoutMs;
        Info = info;
        Cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Cts.CancelAfter(timeoutMs);
    }

    public HandbackCallInfo Info { get; }

    /// <summary>The call's cancellation: its timeout while active, and <see cref="Abort"/>.</summary>
    public CancellationTokenSource Cts { get; }

    /// <summary>Why the call was abandoned, or null when it wasn't (a cancellation is then the timeout).</summary>
    public string? AbortReason => Volatile.Read(ref _abortReason);

    /// <summary>Whether any question of this call has been handed back.</summary>
    public bool HasHandedBack => Volatile.Read(ref _handedBack) == 1;

    /// <summary>Questions answered or declined so far: what was asked, and the names of the fields answered.</summary>
    public IReadOnlyList<PendingEarlierRound> EarlierRounds
    {
        get { lock (_earlierRounds) return [.. _earlierRounds]; }
    }

    public async ValueTask<McpHandbackOutcome> AskAsync(McpHandbackQuestion question, CancellationToken ct)
    {
        var (pending, refusal) = await _store.OpenAsync(this, question, ct).ConfigureAwait(false);
        if (pending is null)
            return McpHandbackOutcome.Decline(refusal ?? "the question could not be handed back");

        // The question waits as long as the agent (or its user) takes, within the TTL; the
        // tool-call timeout bounds only the stretches where the call is actually running.
        Volatile.Write(ref _handedBack, 1);
        TrySetTimeout(Timeout.Infinite);
        _questions.Writer.TryWrite(pending);

        var submission = await pending.Submission.WaitAsync(ct).ConfigureAwait(false);
        if (submission.Target is null)
            return McpHandbackOutcome.Cancel(submission.Outcome.Reason ?? "the question was not answered");

        lock (_earlierRounds)
        {
            _earlierRounds.Add(new PendingEarlierRound
            {
                Message = McpElicitationSchemaDescriber.Flatten(question.Request.Message ?? string.Empty),
                Action = submission.Outcome.Action,
                AnsweredFields = [.. submission.Outcome.Content?.Keys ?? []],
            });
        }

        TrySetTimeout(_timeoutMs);
        return submission.Outcome;
    }

    /// <summary>
    /// Waits until the call completes or hands back another question, and returns the question;
    /// null means the call completed.
    /// </summary>
    public async Task<PendingQuestion?> NextAsync(Task callTask)
    {
        if (_questions.Reader.TryRead(out var ready))
            return ready;

        await Task.WhenAny(callTask, _questions.Reader.WaitToReadAsync().AsTask()).ConfigureAwait(false);
        return _questions.Reader.TryRead(out var question) ? question : null;
    }

    /// <summary>Abandons the call: cancels it and records why. Idempotent; the first reason wins.</summary>
    public void Abort(string reason)
    {
        Interlocked.CompareExchange(ref _abortReason, reason, null);
        try
        {
            Cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call already finished.
        }
    }

    private void TrySetTimeout(int milliseconds)
    {
        try
        {
            Cts.CancelAfter(milliseconds);
        }
        catch (ObjectDisposedException)
        {
            // The call already finished.
        }
    }

    public void Dispose()
    {
        _questions.Writer.TryComplete();
        Cts.Dispose();
    }
}
