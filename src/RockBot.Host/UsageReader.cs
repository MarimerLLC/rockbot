using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Helpers for extracting provider-specific token counts from
/// <see cref="UsageDetails.AdditionalCounts"/>. The OpenAI / Azure Foundry adapter
/// publishes cached input tokens under <c>InputTokenDetails.CachedTokenCount</c>;
/// providers that do not report cache usage simply omit the key.
/// </summary>
internal static class UsageReader
{
    private const string CachedInputTokensKey = "InputTokenDetails.CachedTokenCount";

    /// <summary>
    /// Returns the count of input tokens served from the provider's prompt cache,
    /// or 0 if the provider does not surface cached token information.
    /// </summary>
    public static long GetCachedInputTokens(UsageDetails usage)
    {
        if (usage.AdditionalCounts is { } counts
            && counts.TryGetValue(CachedInputTokensKey, out var cached))
        {
            return cached;
        }
        return 0;
    }
}
