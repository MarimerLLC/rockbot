using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Hosted service that periodically evaluates completed sessions and writes
/// <see cref="FeedbackEntry"/> records with <see cref="FeedbackSignalType.SessionSummary"/>
/// to the <see cref="IFeedbackStore"/>.
///
/// A session is eligible for evaluation once it has been idle for longer than
/// <see cref="FeedbackOptions.SessionIdleThreshold"/>. Each session is evaluated at most
/// once per process lifetime (tracked in a <see cref="HashSet{T}"/>).
/// </summary>
internal sealed class SessionSummaryService : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IConversationMemory _conversationMemory;
    private readonly IFeedbackStore _feedbackStore;
    private readonly ILlmClient _llmClient;
    private readonly FeedbackOptions _options;
    private readonly AgentProfileOptions _profileOptions;
    private readonly ILogger<SessionSummaryService> _logger;

    /// <summary>Sessions already evaluated in this process run (prevents redundant re-evaluation).</summary>
    private readonly HashSet<string> _evaluated = [];

    private Timer? _timer;
    private string? _evaluatorDirective;

    public SessionSummaryService(
        IConversationMemory conversationMemory,
        IFeedbackStore feedbackStore,
        ILlmClient llmClient,
        IOptions<FeedbackOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<SessionSummaryService> logger)
    {
        _conversationMemory = conversationMemory;
        _feedbackStore = feedbackStore;
        _llmClient = llmClient;
        _options = options.Value;
        _profileOptions = profileOptions.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var directivePath = ResolvePath(_options.EvaluatorDirectivePath, _profileOptions.BasePath);
        _evaluatorDirective = File.Exists(directivePath)
            ? File.ReadAllText(directivePath)
            : BuiltInDirective;

        if (!File.Exists(directivePath))
            _logger.LogWarning("SessionSummaryService: evaluator directive not found at {Path}; using built-in fallback", directivePath);
        else
            _logger.LogInformation("SessionSummaryService: loaded evaluator directive from {Path}", directivePath);

        _timer = new Timer(
            state => { _ = EvaluateSessionsAsync(); },
            null,
            _options.PollInterval,
            _options.PollInterval);

        _logger.LogInformation("SessionSummaryService: scheduled — polling every {Interval}", _options.PollInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private async Task EvaluateSessionsAsync()
    {
        try
        {
            var sessionIds = await _conversationMemory.ListSessionsAsync();
            if (sessionIds.Count == 0) return;

            var idleThreshold = DateTimeOffset.UtcNow - _options.SessionIdleThreshold;

            foreach (var sessionId in sessionIds)
            {
                if (_evaluated.Contains(sessionId))
                    continue;

                var turns = await _conversationMemory.GetTurnsAsync(sessionId);
                if (turns.Count == 0)
                    continue;

                var lastTurnAt = turns.Max(t => t.Timestamp);
                if (lastTurnAt > idleThreshold)
                {
                    // Session is still active — skip it this poll cycle
                    continue;
                }

                _logger.LogInformation(
                    "SessionSummaryService: evaluating idle session {SessionId} (last turn: {LastTurn:u})",
                    sessionId, lastTurnAt);

                await EvaluateSessionAsync(sessionId, turns);
                _evaluated.Add(sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionSummaryService: evaluation cycle failed");
        }
    }

    private async Task EvaluateSessionAsync(string sessionId, IReadOnlyList<ConversationTurn> turns)
    {
        // Back off if LLM is busy
        while (!_llmClient.IsIdle)
        {
            _logger.LogDebug("SessionSummaryService: LLM busy, delaying evaluation for session {SessionId}", sessionId);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        var userMessage = new StringBuilder();
        userMessage.AppendLine($"Evaluate the following conversation session ({turns.Count} turns):");
        userMessage.AppendLine();

        foreach (var turn in turns)
            userMessage.AppendLine($"[{turn.Role.ToUpperInvariant()}]: {turn.Content}");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _evaluatorDirective!),
            new(ChatRole.User, userMessage.ToString())
        };

        try
        {
            var response = await _llmClient.GetResponseAsync(messages, new ChatOptions());
            var raw = response.Text?.Trim() ?? string.Empty;
            var json = ExtractJsonObject(raw);

            if (string.IsNullOrEmpty(json))
            {
                _logger.LogWarning("SessionSummaryService: LLM returned no parseable JSON for session {SessionId}", sessionId);
                return;
            }

            var result = JsonSerializer.Deserialize<SessionEvalDto>(json, JsonOptions);
            if (result is null)
            {
                _logger.LogWarning("SessionSummaryService: failed to deserialize evaluation for session {SessionId}", sessionId);
                return;
            }

            var summary = result.Summary ?? "Session evaluated (no summary returned).";
            var entry = new FeedbackEntry(
                Id: Guid.NewGuid().ToString("N")[..12],
                SessionId: sessionId,
                SignalType: FeedbackSignalType.SessionSummary,
                Summary: summary,
                Detail: json,
                Timestamp: DateTimeOffset.UtcNow);

            await _feedbackStore.AppendAsync(entry);

            _logger.LogInformation(
                "SessionSummaryService: evaluated session {SessionId} — quality={Quality}",
                sessionId, result.OverallQuality ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionSummaryService: failed to evaluate session {SessionId}", sessionId);
        }
    }

    private static string ExtractJsonObject(string text)
    {
        // Strip <think>...</think> blocks (DeepSeek reasoning preamble)
        var thinkStart = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        var thinkEnd = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkStart >= 0 && thinkEnd > thinkStart)
            text = text[(thinkEnd + "</think>".Length)..].TrimStart();

        var objStart = text.IndexOf('{');
        var objEnd = text.LastIndexOf('}');
        if (objStart >= 0 && objEnd > objStart)
            return text[objStart..(objEnd + 1)];

        return string.Empty;
    }

    private static string ResolvePath(string path, string basePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(AppContext.BaseDirectory, basePath);

        return Path.Combine(baseDir, path);
    }

    private const string BuiltInDirective = """
        You are a session quality evaluator. Review the conversation turns provided and evaluate the agent's performance.
        Return ONLY a valid JSON object — no markdown, no explanation, no code fences.

        {
          "summary": "one-sentence evaluation of session quality",
          "toolsWorkedWell": ["tool-a"],
          "toolsFailedOrMissed": ["tool-b"],
          "correctionsMade": 0,
          "overallQuality": "good|fair|poor"
        }
        """;

    private sealed record SessionEvalDto(
        string? Summary,
        List<string>? ToolsWorkedWell,
        List<string>? ToolsFailedOrMissed,
        int? CorrectionsMade,
        string? OverallQuality);
}
