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
    /// Returns the tiers that can read images (<see cref="LlmTierOptions.TierSupportsImageInput"/>),
    /// or an empty array when no LLM tiers are configured at all.
    /// </summary>
    /// <remarks>
    /// <see cref="LlmTierOptions.Resolve"/> falls back to Balanced for an unconfigured tier, and a
    /// Balanced chain routes image requests to the entries that can see, so a tier listed here
    /// genuinely reaches a seeing model when asked for.
    /// </remarks>
    public static ModelTier[] From(LlmTierOptions? options) =>
        options is null
            ? []
            : [.. Enum.GetValues<ModelTier>().Where(options.TierSupportsImageInput)];
}
