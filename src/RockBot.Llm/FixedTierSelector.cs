using RockBot.Host;

namespace RockBot.Llm;

/// <summary>
/// An <see cref="ILlmTierSelector"/> that always returns a fixed tier.
/// Used as a fallback when no LLM is configured (echo/stub mode).
/// </summary>
public sealed class FixedTierSelector(ModelTier tier) : ILlmTierSelector
{
    public ModelTier SelectTier(string promptText) => tier;
}
