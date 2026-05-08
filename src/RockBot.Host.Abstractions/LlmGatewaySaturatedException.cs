namespace RockBot.Host;

/// <summary>
/// Thrown by <c>LlmGateway</c> when a tier has reached its bounded queue depth
/// (<c>MaxConcurrent + MaxPending</c>) and a new caller arrives. The caller
/// decides whether to skip the work, defer it, or surface the failure upstream;
/// the gateway does not block indefinitely under saturation.
/// </summary>
public sealed class LlmGatewaySaturatedException : Exception
{
    /// <summary>The tier that was saturated.</summary>
    public ModelTier Tier { get; }

    /// <summary>
    /// The configured cap (<c>MaxConcurrent + MaxPending</c>) that was exceeded.
    /// </summary>
    public int CapacityCap { get; }

    public LlmGatewaySaturatedException(ModelTier tier, int capacityCap)
        : base($"LLM gateway saturated on tier {tier}: " +
               $"in-flight + queued callers exceeded the cap of {capacityCap}.")
    {
        Tier = tier;
        CapacityCap = capacityCap;
    }
}
