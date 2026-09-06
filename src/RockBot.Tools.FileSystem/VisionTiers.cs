using RockBot.Host;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Which configured model tiers accept image input. Shared by the tool registrar (which decides
/// whether to offer <c>analyze_file</c> at all) and the skill guide (which decides whether to
/// document it), so the two cannot disagree regardless of the order their hosted services run.
/// </summary>
internal static class VisionTiers
{
    /// <summary>
    /// Returns the tiers whose resolved config sets <see cref="LlmTierConfig.SupportsImageInput"/>,
    /// or an empty array when no LLM tiers are configured at all.
    /// </summary>
    /// <remarks>
    /// <see cref="LlmTierOptions.Resolve"/> falls back to Balanced for an unconfigured tier, so a
    /// tier listed here genuinely reaches a seeing model when asked for.
    /// </remarks>
    public static ModelTier[] From(LlmTierOptions? options) =>
        options is null
            ? []
            : [.. Enum.GetValues<ModelTier>().Where(t => options.Resolve(t).SupportsImageInput)];
}
