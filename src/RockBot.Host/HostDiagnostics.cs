using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Host;

/// <summary>
/// Centralized diagnostics instrumentation for the agent host pipeline.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
internal static class HostDiagnostics
{
    public const string ActivitySourceName = "RockBot.Host";
    public const string MeterName = "RockBot.Host";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> DispatchDuration =
        Meter.CreateHistogram<double>(
            "rockbot.pipeline.dispatch.duration",
            unit: "ms",
            description: "Duration of message dispatch through the pipeline");
}
