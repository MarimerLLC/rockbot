using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.A2A;

/// <summary>
/// Centralized diagnostics instrumentation for agent-to-agent communication.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
internal static class A2ADiagnostics
{
    public const string ActivitySourceName = "RockBot.A2A";
    public const string MeterName = "RockBot.A2A";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Total A2A task requests dispatched to target agents.</summary>
    public static readonly Counter<long> Requests =
        Meter.CreateCounter<long>(
            "rockbot.a2a.requests",
            unit: "{request}",
            description: "Total A2A task requests dispatched");

    /// <summary>Total A2A task failures (errors received from target agents).</summary>
    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>(
            "rockbot.a2a.failures",
            unit: "{request}",
            description: "Total A2A task failures received");

    /// <summary>Round-trip duration from dispatch to result (or error).</summary>
    public static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(
            "rockbot.a2a.duration",
            unit: "ms",
            description: "Duration of A2A task round-trips from dispatch to result");
}
