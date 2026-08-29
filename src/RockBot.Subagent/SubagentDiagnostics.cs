using System.Diagnostics;
using System.Diagnostics.Metrics;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// Centralized diagnostics instrumentation for the subagent lifecycle.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
internal static class SubagentDiagnostics
{
    public const string ActivitySourceName = "RockBot.Subagent";
    public const string MeterName = "RockBot.Subagent";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    /// <summary>Total subagent tasks spawned.</summary>
    public static readonly Counter<long> Spawns =
        Meter.CreateCounter<long>(
            "rockbot.subagent.spawns",
            unit: "{spawn}",
            description: "Total subagent tasks spawned");

    /// <summary>Currently active (running) subagent tasks.</summary>
    public static readonly UpDownCounter<long> Active =
        Meter.CreateUpDownCounter<long>(
            "rockbot.subagent.active",
            unit: "{subagent}",
            description: "Number of currently active subagent tasks");

    /// <summary>Total execution duration per subagent task.</summary>
    public static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(
            "rockbot.subagent.duration",
            unit: "ms",
            description: "Execution duration of subagent tasks",
            tags: null,
            // Subagents run a full tool loop — measured mean is ~7 minutes, and
            // every observation overflowed the SDK's default 10,000ms ceiling,
            // pinning the Grafana quantiles to a flat 10s. Shares the host's
            // loop scale so turn and subagent durations stay comparable.
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries = HostDiagnostics.LoopDurationBuckets
            });

    /// <summary>Total subagent task failures (exceptions or cancellations).</summary>
    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>(
            "rockbot.subagent.failures",
            unit: "{failure}",
            description: "Total subagent task failures");
}
