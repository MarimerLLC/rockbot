using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace RockBot.Llm.Anthropic;

/// <summary>
/// Builds an <see cref="IChatClient"/> that talks to the Claude API through the official
/// Anthropic SDK rather than its OpenAI compatibility endpoint, which cannot carry
/// <c>cache_control</c> and so never caches.
/// </summary>
public static class AnthropicChatClientFactory
{
    /// <param name="apiKey">Claude API key.</param>
    /// <param name="modelId">Claude model id, e.g. <c>claude-sonnet-5-5</c>.</param>
    /// <param name="endpoint">
    /// Optional base URL. A trailing <c>/v1</c> (the compatibility endpoint's form) is removed,
    /// since the SDK adds it.
    /// </param>
    /// <param name="reasoningEffort">
    /// Tier reasoning effort (<c>minimal</c>, <c>low</c>, <c>medium</c>, <c>high</c>,
    /// <c>xhigh</c>, <c>max</c>); null leaves the model's default. Use
    /// <see cref="TryParseEffort"/> first to report an unusable value.
    /// </param>
    /// <param name="options">Provider-wide settings.</param>
    /// <param name="sdkRetries">
    /// False inside a fallback chain, so a 429 reaches <c>FallbackChatClient</c> at once instead
    /// of being waited out by the SDK.
    /// </param>
    /// <param name="timeout">Per-HTTP-call timeout; null keeps the SDK's 10 minutes.</param>
    public static IChatClient Create(
        string apiKey,
        string modelId,
        string? endpoint,
        string? reasoningEffort,
        AnthropicProviderOptions options,
        bool sdkRetries = true,
        TimeSpan? timeout = null) =>
        Create(apiKey, modelId, endpoint, reasoningEffort, options, sdkRetries, timeout, httpClient: null);

    /// <summary>Test seam: <paramref name="httpClient"/> replaces the SDK's own.</summary>
    internal static IChatClient Create(
        string apiKey,
        string modelId,
        string? endpoint,
        string? reasoningEffort,
        AnthropicProviderOptions options,
        bool sdkRetries,
        TimeSpan? timeout,
        HttpClient? httpClient)
    {
        var clientOptions = new ClientOptions
        {
            ApiKey = apiKey,
            MaxRetries = sdkRetries ? null : 0,
            Timeout = timeout,
        };
        if (httpClient is not null)
            clientOptions.HttpClient = httpClient;
        // Only when given: an explicit BaseUrl also overrides ANTHROPIC_BASE_URL.
        if (NormaliseEndpoint(endpoint) is { } baseUrl)
            clientOptions.BaseUrl = baseUrl;
        var client = new AnthropicClient(clientOptions);

        TryParseEffort(reasoningEffort, out var effort);

        return new AnthropicRequestShapingChatClient(
            client.AsIChatClient(modelId),
            modelId,
            options.DefaultMaxOutputTokens,
            options.PromptCaching ? new CacheControlEphemeral { Ttl = ParseTtl(options.CacheTtl) } : null,
            effort);
    }

    /// <summary>
    /// Maps a tier's <c>ReasoningEffort</c> onto Claude's effort levels. <c>minimal</c> becomes
    /// <c>low</c>, Claude's lowest. Returns false for a value Claude cannot express, including
    /// <c>none</c>: current models can't turn thinking off, and lower effort is the way to cut it.
    /// </summary>
    public static bool TryParseEffort(string? value, out ApiEnum<string, Effort>? effort)
    {
        effort = value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "minimal" or "low" => Effort.Low,
            "medium" => Effort.Medium,
            "high" => Effort.High,
            "xhigh" => Effort.Xhigh,
            "max" => Effort.Max,
            _ => (ApiEnum<string, Effort>?)null,
        };
        return effort is not null || string.IsNullOrWhiteSpace(value);
    }

    internal static Ttl? ParseTtl(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1h" => Ttl.Ttl1h,
        "5m" => Ttl.Ttl5m,
        _ => null, // the API's default, 5 minutes
    };

    internal static string? NormaliseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var trimmed = endpoint.Trim().TrimEnd('/');
        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? trimmed[..^3] : trimmed;
    }
}
