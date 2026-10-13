using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Confirms that a reply really stopped mid-action. The deterministic check
/// (<see cref="AgentLoopRunner.IsIncompleteSetupPhrase"/>: the reply ends with ":" or "...") is
/// only a candidate test — a reply can end on a colon or an ellipsis and be complete. Where a
/// false positive is visible to the user (first-response routing sends "I'm working on that" and
/// re-runs the turn; the native path replaces the reply with a summary; a subagent's report gets
/// "ran out of steps" appended), the decision model (<see cref="DecisionPurposes.IncompleteReply"/>)
/// is asked before acting. No candidate → no call; model unavailable → the candidate stands.
/// </summary>
public sealed class IncompleteReplyCheck(
    IDecisionService? decisions = null,
    ILogger<IncompleteReplyCheck>? logger = null)
{
    private const double DefaultThreshold = 0.5;
    private const int MaxReplyChars = 3000;
    internal const string QuestionName = "stops_before_delivering";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>True when <paramref name="reply"/> stops before delivering what it announces.</summary>
    public async Task<bool> IsIncompleteAsync(string reply, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reply) || !AgentLoopRunner.IsIncompleteSetupPhrase(reply))
            return false;
        if (!_decisions.IsEnabled(DecisionPurposes.IncompleteReply))
            return true;

        var result = await _decisions.DecideAsync(BuildRequest(reply), cancellationToken).ConfigureAwait(false);
        if (result?.Probability(QuestionName) is not { } p)
            return true;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.IncompleteReply, DefaultThreshold);
        _logger.LogInformation(
            "Decision model: reply ending in ':'/'...' stops before delivering? p={Probability:0.000} threshold={Threshold:0.00} → {Outcome}",
            p, threshold, p >= threshold ? "incomplete" : "complete");
        return p >= threshold;
    }

    internal static DecisionRequest BuildRequest(string reply) =>
        DecisionRequest.Single(DecisionPurposes.IncompleteReply,
            reply.Length <= MaxReplyChars ? reply : "…" + reply[^MaxReplyChars..],
            QuestionName,
            new NoulQuestion(
                "This is an AI assistant's whole reply. Does it stop before delivering what it announces — " +
                "ending on a lead-in to a list, result or action that never comes?",
                WhenTrue: "It announces something (\"Here's what I found:\", \"Let me check...\", \"Next I'll:\") " +
                          "and the reply ends there",
                WhenFalse: "Everything it announces is in the reply; the colon or ellipsis is just how a complete " +
                           "reply happens to end"));
}
