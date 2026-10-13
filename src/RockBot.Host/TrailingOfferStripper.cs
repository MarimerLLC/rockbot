using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Removes a throwaway offer ("Would you like me to…", "Let me know if…") from the end of a reply.
/// </summary>
/// <remarks>
/// The regex (<see cref="ResponseSanitizer.FindTrailingOffer"/>) finds the candidate paragraph by
/// its opening words, which also matches questions the user has to answer: "Should I book 3pm or
/// 4pm?" was deleted from the reply the user saw. Before stripping, the decision model
/// (<see cref="DecisionPurposes.TrailingOffer"/>) is asked whether the paragraph is only optional
/// extra help. No candidate → no call. Model unavailable → strip, as before.
/// </remarks>
public sealed class TrailingOfferStripper(
    IDecisionService? decisions = null,
    ILogger<TrailingOfferStripper>? logger = null)
{
    private const double DefaultThreshold = 0.5;
    private const int MaxReplyChars = 2000;
    internal const string QuestionName = "optional_offer";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>The reply without its trailing offer, or unchanged.</summary>
    public async Task<string> StripAsync(string text, CancellationToken cancellationToken = default)
    {
        if (ResponseSanitizer.FindTrailingOffer(text) is not { } split)
            return text;
        if (!_decisions.IsEnabled(DecisionPurposes.TrailingOffer))
            return split.Kept;

        var result = await _decisions.DecideAsync(BuildRequest(split.Kept, split.Offer), cancellationToken)
            .ConfigureAwait(false);
        if (result?.Probability(QuestionName) is not { } p)
            return split.Kept;

        var threshold = _decisions.ThresholdFor(DecisionPurposes.TrailingOffer, DefaultThreshold);
        var strip = p >= threshold;
        _logger.LogInformation(
            "Decision model: trailing paragraph is an optional offer? p={Probability:0.000} threshold={Threshold:0.00} → {Outcome}: {Offer}",
            p, threshold, strip ? "stripped" : "kept", Preview(split.Offer));
        return strip ? split.Kept : text;
    }

    internal static DecisionRequest BuildRequest(string kept, string offer) =>
        DecisionRequest.Single(DecisionPurposes.TrailingOffer,
            new Dictionary<string, object?>
            {
                ["reply_before_it"] = kept.Length <= MaxReplyChars ? kept : "…" + kept[^MaxReplyChars..],
                ["trailing_paragraph"] = offer,
            },
            QuestionName,
            new NoulQuestion(
                "An assistant's reply ends with trailing_paragraph. Can it be removed without losing anything " +
                "the user needs?",
                WhenTrue: "It only offers optional extra help (\"Would you like me to…\", \"Let me know if…\", " +
                          "\"I can also…\"); the reply is complete without it",
                WhenFalse: "It asks a question the user must answer for the task to go on (a choice between " +
                           "options, a missing detail, a confirmation before acting), or it carries results, " +
                           "instructions or a caveat the user needs"));

    private static string Preview(string text) => text.Length <= 120 ? text : text[..120] + "…";
}
