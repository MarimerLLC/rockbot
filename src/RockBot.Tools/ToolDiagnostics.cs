using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Tools;

/// <summary>
/// Centralized diagnostics instrumentation for the tool execution layer.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
internal static class ToolDiagnostics
{
    public const string ActivitySourceName = "RockBot.Tools";
    public const string MeterName = "RockBot.Tools";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> InvokeDuration =
        Meter.CreateHistogram<double>(
            "rockbot.tool.invoke.duration",
            unit: "ms",
            description: "Duration of tool invocation operations");

    public static readonly Counter<long> Invocations =
        Meter.CreateCounter<long>(
            "rockbot.tool.invocations",
            unit: "{invocation}",
            description: "Total number of tool invocations");
}
