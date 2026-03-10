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

    // ── LLM metrics — recorded in LlmClient (the actual call path) ───────────
    public static readonly Histogram<double> LlmRequestDuration =
        Meter.CreateHistogram<double>(
            "rockbot.llm.request.duration",
            unit: "ms",
            description: "Duration of LLM request operations");

    public static readonly Counter<long> LlmRequests =
        Meter.CreateCounter<long>(
            "rockbot.llm.requests",
            unit: "{request}",
            description: "Total number of LLM requests");

    public static readonly Counter<long> LlmTokenInput =
        Meter.CreateCounter<long>(
            "rockbot.llm.token.input",
            unit: "{token}",
            description: "Total number of input tokens consumed");

    public static readonly Counter<long> LlmTokenOutput =
        Meter.CreateCounter<long>(
            "rockbot.llm.token.output",
            unit: "{token}",
            description: "Total number of output tokens produced");
}
