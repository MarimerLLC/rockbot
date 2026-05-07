namespace RockBot.Host;

/// <summary>
/// Options for <c>LlmGateway</c>: the global per-tier concurrency layer that all LLM
/// calls flow through. See <c>design/llm-gateway.md</c> for the full design rationale.
/// </summary>
/// <remarks>
/// Caps are per-process. Across multiple agent processes against the same provider
/// account, total concurrency is the sum. Per-account rate limits ultimately bound the
/// system; the gateway is a per-process governor, not a global one.
/// </remarks>
public sealed class LlmGatewayOptions
{
    /// <summary>
    /// Maximum concurrent in-flight LLM calls on the <see cref="ModelTier.Low"/> tier.
    /// Cheap calls used heavily for batch/extraction work, so a higher cap is appropriate.
    /// </summary>
    public int LowMaxConcurrent { get; set; } = 8;

    /// <summary>
    /// Maximum concurrent in-flight LLM calls on the <see cref="ModelTier.Balanced"/> tier.
    /// </summary>
    public int BalancedMaxConcurrent { get; set; } = 4;

    /// <summary>
    /// Maximum concurrent in-flight LLM calls on the <see cref="ModelTier.High"/> tier.
    /// Expensive judgment calls; lower cap.
    /// </summary>
    public int HighMaxConcurrent { get; set; } = 2;

    /// <summary>
    /// Maximum number of retry attempts on rate-limit (HTTP 429) responses before
    /// the call surfaces the failure to the caller. Each retry honors any
    /// <c>Retry-After</c> response header; in its absence, exponential backoff
    /// (1s, 2s, 4s, 8s, ...) is used, capped by <see cref="MaxBackoffSeconds"/>.
    /// Set to zero to disable retry on rate-limit errors.
    /// </summary>
    public int MaxRateLimitRetries { get; set; } = 5;

    /// <summary>
    /// Maximum backoff (in seconds) between retry attempts when no
    /// <c>Retry-After</c> header is supplied by the provider. Caps the
    /// exponential growth of fallback backoff.
    /// </summary>
    public int MaxBackoffSeconds { get; set; } = 16;
}
