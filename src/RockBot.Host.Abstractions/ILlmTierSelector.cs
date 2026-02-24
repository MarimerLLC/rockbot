namespace RockBot.Host;

/// <summary>
/// Selects the appropriate <see cref="ModelTier"/> for a given prompt.
/// Implementations may use keyword heuristics, embeddings, or fixed rules.
/// </summary>
public interface ILlmTierSelector
{
    /// <summary>
    /// Returns the tier best suited for <paramref name="promptText"/>.
    /// </summary>
    ModelTier SelectTier(string promptText);
}
