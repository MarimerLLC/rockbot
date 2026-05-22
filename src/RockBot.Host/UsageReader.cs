using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Helpers for extracting provider-specific token counts from
/// <see cref="UsageDetails.AdditionalCounts"/>. The OpenAI / Azure Foundry adapter
/// publishes cached input tokens under <c>InputTokenDetails.CachedTokenCount</c>;
/// providers that do not report cache usage simply omit the key. Several candidate
/// key names are checked because the spelling has shifted across M.E.AI releases.
/// </summary>
internal static class UsageReader
{
    /// <summary>
    /// Candidate keys for cached input tokens in <see cref="UsageDetails.AdditionalCounts"/>,
    /// tried in order. The first three are the MEAI dotted spelling, an older flat spelling,
    /// and the raw OpenAI Chat Completion API field name (just in case the adapter passes
    /// through unchanged).
    /// </summary>
    private static readonly string[] CachedInputTokensKeys =
    {
        "InputTokenDetails.CachedTokenCount",
        "CachedTokenCount",
        "cached_tokens",
        "prompt_tokens_details.cached_tokens",
    };

    /// <summary>
    /// Returns the count of input tokens served from the provider's prompt cache,
    /// or 0 if the provider does not surface cached token information.
    /// </summary>
    public static long GetCachedInputTokens(UsageDetails usage)
    {
        if (usage.AdditionalCounts is not { } counts) return 0;
        foreach (var key in CachedInputTokensKeys)
        {
            if (counts.TryGetValue(key, out var cached)) return cached;
        }
        return 0;
    }

    /// <summary>
    /// Returns a diagnostic string of all keys and values present in
    /// <see cref="UsageDetails.AdditionalCounts"/>, or an empty string when none are
    /// present. Used to confirm what the provider/adapter is actually publishing when
    /// the canonical cached-token key returns 0.
    /// </summary>
    public static string DescribeAdditionalCounts(UsageDetails usage)
    {
        if (usage.AdditionalCounts is not { Count: > 0 } counts) return string.Empty;
        return string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
