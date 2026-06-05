using System.Diagnostics.Metrics;

namespace RockBot.Wisp;

/// <summary>
/// Diagnostics instrumentation for the wisp subsystem. Uses a BCL <see cref="Meter"/>,
/// which is zero-cost when no listener is attached.
/// </summary>
public static class WispDiagnostics
{
    public const string MeterName = "RockBot.Wisp";

    public static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Incremented each time <see cref="WispDispatchCircuitBreaker"/> refuses a dispatch.
    /// A non-zero rate is the signature of a runaway re-dispatch loop — worth alerting on.
    /// </summary>
    public static readonly Counter<long> CircuitBreakerTrips =
        Meter.CreateCounter<long>(
            "rockbot.wisp.circuit_breaker.trips",
            unit: "{trip}",
            description: "Number of wisp dispatches refused by the dispatch circuit breaker");
}
