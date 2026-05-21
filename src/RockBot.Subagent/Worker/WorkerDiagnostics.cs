using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Subagent.Worker;

/// <summary>
/// Centralized diagnostics for the worker (lean rung) lifecycle. Mirrors
/// <see cref="SubagentDiagnostics"/> with a <c>subagent_type=worker</c> tag so
/// cost comparison dashboards can stack worker and subagent runs side by side.
/// </summary>
internal static class WorkerDiagnostics
{
    public const string ActivitySourceName = "RockBot.Worker";
    public const string MeterName = "RockBot.Worker";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Total worker tasks spawned.</summary>
    public static readonly Counter<long> Spawns =
        Meter.CreateCounter<long>(
            "rockbot.worker.spawns",
            unit: "{spawn}",
            description: "Total worker tasks spawned");

    /// <summary>Total execution duration per worker task.</summary>
    public static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(
            "rockbot.worker.duration",
            unit: "ms",
            description: "Execution duration of worker tasks");

    /// <summary>Total worker task failures (exceptions, cancellations, timeouts).</summary>
    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>(
            "rockbot.worker.failures",
            unit: "{failure}",
            description: "Total worker task failures");
}
