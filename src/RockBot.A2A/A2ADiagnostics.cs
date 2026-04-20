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

    /// <summary>Number of InputRequired follow-up rounds completed.</summary>
    public static readonly Counter<long> InputRequiredRounds =
        Meter.CreateCounter<long>(
            "rockbot.a2a.input_required_rounds",
            unit: "{round}",
            description: "A2A InputRequired follow-up rounds completed");

    /// <summary>Number of InputRequired loops terminated by max-round or repetition limits.</summary>
    public static readonly Counter<long> InputRequiredBreaks =
        Meter.CreateCounter<long>(
            "rockbot.a2a.input_required_breaks",
            unit: "{break}",
            description: "A2A InputRequired loops terminated by safety limits");

    /// <summary>Number of GetTask polling attempts for long-running HTTP tasks.</summary>
    public static readonly Counter<long> PollingAttempts =
        Meter.CreateCounter<long>(
            "rockbot.a2a.polling_attempts",
            unit: "{attempt}",
            description: "A2A GetTask polling attempts for long-running tasks");

    /// <summary>Number of streaming events received during outbound A2A streaming dispatch.</summary>
    public static readonly Counter<long> StreamingEvents =
        Meter.CreateCounter<long>(
            "rockbot.a2a.streaming_events",
            unit: "{event}",
            description: "Streaming events received during outbound A2A dispatch");

    /// <summary>Number of times SubscribeToTask failed and fell back to GetTask polling.</summary>
    public static readonly Counter<long> SubscribeFallbacks =
        Meter.CreateCounter<long>(
            "rockbot.a2a.subscribe_fallbacks",
            unit: "{fallback}",
            description: "SubscribeToTask failures that fell back to GetTask polling");
}
