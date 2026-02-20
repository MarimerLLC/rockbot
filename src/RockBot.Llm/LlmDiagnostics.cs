using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Llm;

/// <summary>
/// Centralized diagnostics instrumentation for the LLM layer.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
internal static class LlmDiagnostics
{
    public const string ActivitySourceName = "RockBot.Llm";
    public const string MeterName = "RockBot.Llm";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "rockbot.llm.request.duration",
            unit: "ms",
            description: "Duration of LLM request operations");

    public static readonly Counter<long> Requests =
        Meter.CreateCounter<long>(
            "rockbot.llm.requests",
            unit: "{request}",
            description: "Total number of LLM requests");

    public static readonly Counter<long> TokenInput =
        Meter.CreateCounter<long>(
            "rockbot.llm.token.input",
            unit: "{token}",
            description: "Total number of input tokens consumed");

    public static readonly Counter<long> TokenOutput =
        Meter.CreateCounter<long>(
            "rockbot.llm.token.output",
            unit: "{token}",
            description: "Total number of output tokens produced");
}
