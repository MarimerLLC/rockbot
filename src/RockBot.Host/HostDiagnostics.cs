using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Host;

/// <summary>
/// Centralized diagnostics instrumentation for the agent host pipeline.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
public static class HostDiagnostics
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

    // ── Agent turn metrics — recorded at architectural boundaries ─────────────

    /// <summary>Duration from user message receipt to final reply published.</summary>
    public static readonly Histogram<double> TurnDuration =
        Meter.CreateHistogram<double>(
            "rockbot.agent.turn.duration",
            unit: "ms",
            description: "Duration of agent turns from message receipt to final reply");

    /// <summary>Total agent turns completed.</summary>
    public static readonly Counter<long> Turns =
        Meter.CreateCounter<long>(
            "rockbot.agent.turns",
            unit: "{turn}",
            description: "Total agent turns completed");

    /// <summary>Estimated token count of context injected at turn start.</summary>
    public static readonly Histogram<long> TurnContextTokens =
        Meter.CreateHistogram<long>(
            "rockbot.agent.context.tokens",
            unit: "{token}",
            description: "Estimated token count of context injected before LLM call");

    /// <summary>Total input tokens consumed per turn (aggregated across all LLM calls).</summary>
    public static readonly Histogram<long> TurnTokensInput =
        Meter.CreateHistogram<long>(
            "rockbot.agent.turn.tokens.input",
            unit: "{token}",
            description: "Input tokens per turn, aggregated across all LLM calls in the loop");

    /// <summary>Total output tokens produced per turn (aggregated across all LLM calls).</summary>
    public static readonly Histogram<long> TurnTokensOutput =
        Meter.CreateHistogram<long>(
            "rockbot.agent.turn.tokens.output",
            unit: "{token}",
            description: "Output tokens per turn, aggregated across all LLM calls in the loop");

    /// <summary>Number of tool calls executed per turn.</summary>
    public static readonly Histogram<long> TurnToolCalls =
        Meter.CreateHistogram<long>(
            "rockbot.agent.turn.tools",
            unit: "{call}",
            description: "Tool calls executed per turn");

    // ── FinOps ────────────────────────────────────────────────────────────────

    /// <summary>Estimated USD cost per LLM call, labelled by model and tier.</summary>
    public static readonly Counter<double> LlmCostUsd =
        Meter.CreateCounter<double>(
            "rockbot.llm.cost.usd",
            unit: "USD",
            description: "Estimated USD cost of LLM calls based on published token pricing");

    /// <summary>
    /// Per-request USD cost as a histogram. Supports exemplars (trace_id linkage) and
    /// distribution analysis. Use this to click a cost spike in Grafana and jump to the
    /// trace that caused it.
    /// </summary>
    public static readonly Histogram<double> LlmCostPerRequest =
        Meter.CreateHistogram<double>(
            "rockbot.llm.cost.per_request",
            unit: "USD",
            description: "USD cost per individual LLM request — histogram enables exemplar trace linkage");
}
