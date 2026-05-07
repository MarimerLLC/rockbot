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
}
