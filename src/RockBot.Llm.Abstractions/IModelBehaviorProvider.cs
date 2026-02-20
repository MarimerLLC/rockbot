namespace RockBot.Llm;

/// <summary>
/// Resolves <see cref="ModelBehavior"/> for a given model ID.
/// Supports exact-match and prefix-match lookup against configured overrides,
/// so a single entry like "deepseek" covers all DeepSeek model variants.
/// </summary>
public interface IModelBehaviorProvider
{
    /// <summary>
    /// Returns the <see cref="ModelBehavior"/> for the given model identifier.
    /// Falls back to <see cref="ModelBehavior.Default"/> when no override matches.
    /// </summary>
    ModelBehavior GetBehavior(string? modelId);
}
