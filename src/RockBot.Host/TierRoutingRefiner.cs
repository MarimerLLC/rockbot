using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Llm;

namespace RockBot.Host;

/// <summary>
/// Second opinion on model-tier routing. The keyword selector (<c>KeywordTierSelector</c>) scores
/// a message from keyword lists, length and a few regexes, and has a long misfire history (#663,
/// #471, #486, #383). Its verdict stands unless the score sat close to a tier ceiling and no
/// deterministic rule (thread floor, trivial guard, inheritance) decided the tier; then the
/// decision model (<see cref="DecisionPurposes.TierRouting"/>) picks Low/Balanced/High, at most one
/// step from the keyword tier, and only when it is confident.
/// </summary>
/// <remarks>
/// This runs before the first LLM call of a turn, so it is deliberately narrow: messages far from
/// a boundary, or routed by thread state, make no call.
/// </remarks>
public sealed class TierRoutingRefiner(
    IDecisionService? decisions = null,
    IOptions<DecisionModelOptions>? options = null,
    ILogger<TierRoutingRefiner>? logger = null)
{
    private const double DefaultConfidence = 0.6;
    private const int MaxPromptChars = 3000;
    internal const string QuestionName = "tier";

    private readonly IDecisionService _decisions = decisions ?? NullDecisionService.Instance;
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private double Margin => options?.Value.TierRoutingMargin ?? DecisionModelOptions.DefaultTierRoutingMargin;

    /// <summary>
    /// <paramref name="classification"/>, or a copy with the decision model's tier and rule
    /// <see cref="TierRoutingRules.DecisionModel"/>.
    /// </summary>
    public async Task<TierClassification> RefineAsync(
        string prompt, string origin, TierClassification classification, CancellationToken cancellationToken = default)
    {
        if (!ShouldAsk(classification, Margin) || !_decisions.IsEnabled(DecisionPurposes.TierRouting))
            return classification;

        var result = await _decisions.DecideAsync(BuildRequest(prompt, origin), cancellationToken).ConfigureAwait(false);
        if (result?.Answers.GetValueOrDefault(QuestionName) is not { Choice: { } choice } answer
            || !TryParse(choice, out var modelTier))
            return classification;

        var confidence = answer.Probabilities?.GetValueOrDefault(choice) ?? answer.Confidence ?? 0;
        var threshold = _decisions.ThresholdFor(DecisionPurposes.TierRouting, DefaultConfidence);
        // One step at most: the model breaks a near-tie, it does not overrule the score outright.
        var refined = (ModelTier)Math.Clamp((int)modelTier, (int)classification.Tier - 1, (int)classification.Tier + 1);

        _logger.LogInformation(
            "Decision model: tier for {Origin} near a boundary (score={Score:F3}, distance={Distance:F3}) — keyword={Keyword}, " +
            "model={Model} p={Confidence:0.000} threshold={Threshold:0.00} → {Outcome}",
            origin, classification.ComplexityScore, classification.BoundaryDistance, classification.Tier, modelTier,
            confidence, threshold, confidence >= threshold && refined != classification.Tier ? refined : classification.Tier);

        if (confidence < threshold || refined == classification.Tier)
            return classification;

        return classification with { Tier = refined, IntrinsicTier = refined, Rule = TierRoutingRules.DecisionModel };
    }

    /// <summary>True when the keyword score decided the tier and sat within <paramref name="margin"/> of a ceiling.</summary>
    internal static bool ShouldAsk(TierClassification classification, double margin) =>
        classification.Rule is TierRoutingRules.ScoreBand or TierRoutingRules.HighGate
        && classification.BoundaryDistance is { } distance
        && distance <= margin;

    internal static DecisionRequest BuildRequest(string prompt, string origin) =>
        DecisionRequest.Single(DecisionPurposes.TierRouting,
            new Dictionary<string, object?>
            {
                ["request"] = prompt.Length <= MaxPromptChars ? prompt : prompt[..MaxPromptChars] + "…",
                ["source"] = origin == "subagent" ? "a task delegated to a sub-agent" : "a user's chat message",
            },
            QuestionName,
            new ChoiceQuestion(
                "Which model tier should handle this request for an AI assistant with tools (mail, calendar, " +
                "files, web search)?",
                new Dictionary<string, string>
                {
                    ["low"] = "Greetings, acknowledgements, quick facts and simple single-step lookups",
                    ["balanced"] = "Ordinary tasks: tool use, scheduling, drafting, summarising, multi-step " +
                                   "operations with clear instructions",
                    ["high"] = "Hard problems: deep research and synthesis, careful reasoning, debugging, " +
                               "architecture or analysis where quality matters more than speed",
                }));

    private static bool TryParse(string choice, out ModelTier tier)
    {
        switch (choice.Trim().ToLowerInvariant())
        {
            case "low": tier = ModelTier.Low; return true;
            case "balanced": tier = ModelTier.Balanced; return true;
            case "high": tier = ModelTier.High; return true;
            default: tier = default; return false;
        }
    }
}
