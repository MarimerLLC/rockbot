using RockBot.Host;

namespace RockBot.Llm;

/// <summary>
/// An <see cref="ILlmTierSelector"/> that always returns a fixed tier.
/// Used as a fallback when no LLM is configured (echo/stub mode), and for
/// <c>LLM:FixedTier</c> pinning. Thread-state floors and inherited tiers (#663) do not
/// apply: a pinned tier is pinned.
/// </summary>
public sealed class FixedTierSelector(ModelTier tier) : ILlmTierSelector
{
    public ModelTier SelectTier(string promptText) => tier;

    public TierClassification Classify(string promptText) =>
        new(tier, 0.0, [], []) { Rule = TierRoutingRules.Fixed };

    public TierClassification Classify(string promptText, TierRoutingContext context) =>
        new(tier, 0.0, [], []) { Rule = TierRoutingRules.Fixed };
}
