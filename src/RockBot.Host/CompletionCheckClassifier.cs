using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Second opinion for the completion-evaluator gate (<see cref="CompletionEvalTriggers.Decide"/>).
/// The gate's verb-list regexes miss replies they have no words for (#666 measured the evaluator
/// running on 1 of 31 loops; #686 and #690 each widened them after misses), and a miss ships the
/// reply unchecked. When the regexes find nothing on a user turn, this asks the decision model
/// (<see cref="DecisionPurposes.CompletionCheck"/>) three questions about the reply in one call,
/// and maps a yes onto the trigger whose re-prompt guidance fits.
/// </summary>
public sealed class CompletionCheckClassifier(
    IDecisionService? decisions = null,
    ILogger<CompletionCheckClassifier>? logger = null)
{
    private const double DefaultThreshold = 0.5;
    private const int MaxReplyChars = 4000;
    private const int MaxUserChars = 2000;
    private const int MaxCallsListed = 30;

    internal const string Promise = "promises_unfinished_work";
    internal const string UnbackedClaim = "claims_change_no_call_made";
    internal const string Denial = "denies_capability";

    private const int MaxCached = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    // A no-tool first reply that the fast path flags is re-run in the background loop, whose
    // completion check would ask about the same reply again. Answers are kept briefly by content.
    private readonly ConcurrentDictionary<string, (CompletionEvalTrigger Trigger, DateTimeOffset At)> _recent = new();

    /// <summary>True when the model is configured and this use is on.</summary>
    public bool IsEnabled => _decisions.IsEnabled(DecisionPurposes.CompletionCheck);

    /// <summary>
    /// The trigger the model's answers point to, or <see cref="CompletionEvalTrigger.None"/> when
    /// it sees nothing to check, is unavailable, or the exchange is small talk.
    /// </summary>
    public async Task<CompletionEvalTrigger> SecondOpinionAsync(
        string response, string? userMessage, IReadOnlyList<LoopToolCall> toolCalls,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(response))
            return CompletionEvalTrigger.None;
        // "thanks" → "You're welcome!" needs no second look.
        if (CompletionEvalTriggers.IsTrivialChat(userMessage))
            return CompletionEvalTrigger.None;

        var key = CacheKey(response, userMessage, toolCalls);
        if (_recent.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < CacheLifetime)
            return cached.Trigger;

        var result = await _decisions.DecideAsync(BuildRequest(response, userMessage, toolCalls), cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            return CompletionEvalTrigger.None;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.CompletionCheck, DefaultThreshold);
        var noTools = CompletionEvalTriggers.SubstantiveCalls(toolCalls).Count == 0;
        var promise = result.Probability(Promise) ?? 0;
        var claim = result.Probability(UnbackedClaim) ?? 0;
        var denial = result.Probability(Denial) ?? 0;

        var trigger =
            claim >= threshold ? CompletionEvalTrigger.ClaimedChange
            : promise >= threshold && noTools ? CompletionEvalTrigger.PromiseNoAction
            : denial >= threshold ? CompletionEvalTrigger.Pattern
            : CompletionEvalTrigger.None;

        _logger.LogInformation(
            "Decision model: completion check promise={Promise:0.000} unbacked-claim={Claim:0.000} denial={Denial:0.000} " +
            "threshold={Threshold:0.00} → {Trigger}",
            promise, claim, denial, threshold, CompletionEvalTriggers.LogName(trigger));

        if (_recent.Count >= MaxCached)
            foreach (var stale in _recent.Where(e => DateTimeOffset.UtcNow - e.Value.At >= CacheLifetime).Select(e => e.Key).ToList())
                _recent.TryRemove(stale, out _);
        if (_recent.Count < MaxCached)
            _recent[key] = (trigger, DateTimeOffset.UtcNow);
        return trigger;
    }

    private static string CacheKey(string response, string? userMessage, IReadOnlyList<LoopToolCall> toolCalls) =>
        $"{userMessage}\u0000{response}\u0000{CompletionEvalTriggers.SubstantiveCalls(toolCalls).Count}";

    internal static DecisionRequest BuildRequest(string response, string? userMessage, IReadOnlyList<LoopToolCall> toolCalls)
    {
        var calls = toolCalls.Take(MaxCallsListed)
            .Select(c => $"{ToolSideEffects.EffectiveToolName(c.Name, c.Arguments)}: " +
                         $"{(c.Succeeded ? "succeeded" : "failed")}" +
                         $"{(c.Succeeded && ToolSideEffects.IsSideEffecting(c) ? ", changed state" : "")}")
            .ToList();
        if (toolCalls.Count > MaxCallsListed)
            calls.Add($"…and {toolCalls.Count - MaxCallsListed} more");

        return new DecisionRequest(DecisionPurposes.CompletionCheck,
            new Dictionary<string, object?>
            {
                ["user_message"] = Truncate(userMessage ?? "", MaxUserChars),
                ["assistant_reply"] = Truncate(response, MaxReplyChars),
                ["tool_calls_this_turn"] = calls,
            },
            new Dictionary<string, DecisionQuestion>
            {
                [Promise] = new NoulQuestion(
                    "Does assistant_reply say the assistant will do, is about to do, or should have done some " +
                    "work, instead of reporting work it finished?",
                    WhenTrue: "It promises or describes work (\"I'll…\", \"Let me…\", \"I'm going to…\", \"I should " +
                              "have…\") rather than reporting a result",
                    WhenFalse: "It reports results or answers the question; mentioning a next step the user may " +
                               "choose does not count"),
                [UnbackedClaim] = new NoulQuestion(
                    "Does assistant_reply claim the assistant created, changed, sent, scheduled, saved, " +
                    "deleted or verified something that no entry in tool_calls_this_turn did?",
                    WhenTrue: "It reports an action or check as done, and no listed call that succeeded could " +
                              "have done it",
                    WhenFalse: "Every action it reports matches a successful listed call, or it reports no actions"),
                [Denial] = new NoulQuestion(
                    "Does assistant_reply say the assistant cannot access, reach, use or do something the user " +
                    "asked about?"),
            });
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
