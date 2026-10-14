using Microsoft.Extensions.AI;

namespace RockBot.Host;

/// <summary>
/// Helpers for extracting provider-specific token counts from <see cref="UsageDetails"/>.
/// In Microsoft.Extensions.AI 10.3.0 the OpenAI adapter publishes cached input tokens via
/// the first-class <see cref="UsageDetails.CachedInputTokenCount"/> property; earlier
/// previews staged the same value under <c>AdditionalCounts["InputTokenDetails.CachedTokenCount"]</c>.
/// Both paths are checked so deployments pinned to either version surface a non-zero
/// value once the provider returns one.
/// </summary>
internal static class UsageReader
{
    /// <summary>
    /// Fallback keys for cached input tokens in <see cref="UsageDetails.AdditionalCounts"/>,
    /// used only when the first-class property is not populated. Order: MEAI dotted
    /// spelling, older flat spelling, raw OpenAI Chat Completion API field name, nested.
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
        if (usage.CachedInputTokenCount is { } cached && cached > 0)
            return cached;

        if (usage.AdditionalCounts is { } counts)
        {
            foreach (var key in CachedInputTokensKeys)
            {
                if (counts.TryGetValue(key, out var fallback)) return fallback;
            }
        }
        return 0;
    }

    /// <summary>
    /// Returns the input tokens written to the provider's prompt cache, or 0 when the provider
    /// doesn't report writes. The Anthropic adapter publishes them as
    /// <c>AdditionalCounts["CacheCreationInputTokens"]</c>; OpenAI caching has no write charge.
    /// </summary>
    public static long GetCacheWriteTokens(UsageDetails usage) =>
        usage.AdditionalCounts?.TryGetValue(CacheWriteKey, out var v) == true ? v : 0;

    /// <summary>
    /// Returns the part of <see cref="GetCacheWriteTokens"/> written with the 1-hour TTL (billed
    /// at 2x input rather than 1.25x). The SDK adapter doesn't split writes by TTL, so the
    /// Anthropic provider adds this count from the raw response.
    /// </summary>
    public static long GetCacheWrite1hTokens(UsageDetails usage) =>
        usage.AdditionalCounts?.TryGetValue(CacheWrite1hKey, out var v) == true ? v : 0;

    private const string CacheWriteKey = "CacheCreationInputTokens";
    private const string CacheWrite1hKey = "CacheCreation1hInputTokens";

    /// <summary>
    /// Returns a diagnostic string of all keys and values present in
    /// <see cref="UsageDetails.AdditionalCounts"/>, or an empty string when none are
    /// present. Used to confirm what the provider/adapter is actually publishing when
    /// the canonical cached-token reads return 0.
    /// </summary>
    public static string DescribeAdditionalCounts(UsageDetails usage)
    {
        if (usage.AdditionalCounts is not { Count: > 0 } counts) return string.Empty;
        return string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
