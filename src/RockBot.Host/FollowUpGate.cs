using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Decides whether a finished turn is worth a follow-up evaluation. The follow-up evaluator is a
/// full Low-tier LLM call on most completed user turns, and its own prompt says closed or specific
/// requests "almost NEVER warrant follow-ups". After the deterministic skips (follow-ups disabled,
/// complexity ≤ 0.15), the decision model (<see cref="DecisionPurposes.FollowUp"/>) is asked whether
/// the request was open or exploratory with more to explore; a confident no skips the LLM call.
/// Model unavailable → evaluate, as before. This saves more LLM calls than it costs decision calls.
/// </summary>
public sealed class FollowUpGate(
    IDecisionService? decisions = null,
    ILogger<FollowUpGate>? logger = null)
{
    // Skip the evaluator only on a confident no: a missed follow-up costs little, but the gate
    // should not second-guess borderline exploratory requests.
    private const double DefaultThreshold = 0.3;
    private const int MaxFieldChars = 2000;
    internal const string QuestionName = "worth_following_up";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>False when the decision model is confident there is nothing worth following up.</summary>
    public async Task<bool> WorthEvaluatingAsync(string userRequest, string response, CancellationToken cancellationToken = default)
    {
        if (!_decisions.IsEnabled(DecisionPurposes.FollowUp))
            return true;

        var result = await _decisions.DecideAsync(BuildRequest(userRequest, response), cancellationToken).ConfigureAwait(false);
        if (result?.Probability(QuestionName) is not { } p)
            return true;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.FollowUp, DefaultThreshold);
        _logger.LogInformation(
            "Decision model: follow-up worth evaluating? p={Probability:0.000} threshold={Threshold:0.00} → {Outcome}",
            p, threshold, p >= threshold ? "evaluate" : "skip");
        return p >= threshold;
    }

    internal static DecisionRequest BuildRequest(string userRequest, string response) =>
        DecisionRequest.Single(DecisionPurposes.FollowUp,
            new Dictionary<string, object?>
            {
                ["user_request"] = Truncate(userRequest),
                ["assistant_reply"] = Truncate(response),
            },
            QuestionName,
            new NoulQuestion(
                "Did user_request ask the assistant to investigate, research, catch up on, or connect information " +
                "across sources, leaving more of that exploration worth doing after assistant_reply?",
                WhenTrue: "Open or exploratory request (\"what's going on with the Henderson project?\", \"catch me " +
                          "up on what I missed\") where related information could still be connected",
                WhenFalse: "A closed or specific question or task (\"what's on my todo list?\", \"cancel my 3pm\") " +
                           "that the reply answered or did"));

    private static string Truncate(string text) => text.Length <= MaxFieldChars ? text : text[..MaxFieldChars] + "…";
}
