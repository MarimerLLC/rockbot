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

    /// <summary>
    /// Time a caller spent waiting for a per-tier gateway slot before its LLM call
    /// could proceed. Non-zero values indicate contention; sustained high values
    /// indicate the tier's <c>MaxConcurrent</c> cap is too low for the workload
    /// (or that callers are issuing too many parallel calls).
    /// </summary>
    public static readonly Histogram<double> LlmGatewaySlotWaitDuration =
        Meter.CreateHistogram<double>(
            "rockbot.llm.gateway.slot_wait.duration",
            unit: "ms",
            description: "Time spent waiting for a per-tier LLM gateway slot");

    /// <summary>
    /// Number of rate-limit retries performed by the LLM gateway. Tagged by tier
    /// and by retry-after source (<c>"header"</c> when the provider supplied a
    /// <c>Retry-After</c> hint, <c>"backoff"</c> when the gateway used its
    /// exponential-backoff fallback).
    /// </summary>
    public static readonly Counter<long> LlmGatewayRateLimitRetries =
        Meter.CreateCounter<long>(
            "rockbot.llm.gateway.rate_limit_retries",
            unit: "{retry}",
            description: "Number of rate-limit (429) retries performed by the LLM gateway");

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

    // ── Completion evaluator ────────────────────────────────────────────────

    /// <summary>Evaluator determined the task was complete.</summary>
    public static readonly Counter<long> CompletionCheckComplete =
        Meter.CreateCounter<long>(
            "rockbot.agent.completion_check.complete",
            unit: "{check}",
            description: "Completion evaluator determined task was done");

    /// <summary>Evaluator determined the task was incomplete — triggered a re-prompt.</summary>
    public static readonly Counter<long> CompletionCheckIncomplete =
        Meter.CreateCounter<long>(
            "rockbot.agent.completion_check.incomplete",
            unit: "{check}",
            description: "Completion evaluator triggered a re-prompt");

    /// <summary>Completion evaluation was skipped (force termination).</summary>
    public static readonly Counter<long> CompletionCheckSkipped =
        Meter.CreateCounter<long>(
            "rockbot.agent.completion_check.skipped",
            unit: "{check}",
            description: "Completion evaluation skipped due to force termination");

    // ── Follow-up passes ──────────────────────────────────────────────────

    /// <summary>Follow-up evaluator found proactive opportunities — triggered a follow-up pass.</summary>
    public static readonly Counter<long> FollowUpTriggered =
        Meter.CreateCounter<long>(
            "rockbot.agent.follow_up.triggered",
            unit: "{pass}",
            description: "Follow-up evaluator triggered a proactive follow-up pass");

    /// <summary>Follow-up evaluator found no opportunities — no follow-up needed.</summary>
    public static readonly Counter<long> FollowUpNone =
        Meter.CreateCounter<long>(
            "rockbot.agent.follow_up.none",
            unit: "{check}",
            description: "Follow-up evaluator found no proactive opportunities");

    /// <summary>Follow-up evaluation was skipped (disabled, force termination, or re-prompt path).</summary>
    public static readonly Counter<long> FollowUpSkipped =
        Meter.CreateCounter<long>(
            "rockbot.agent.follow_up.skipped",
            unit: "{check}",
            description: "Follow-up evaluation skipped");

    // ── Embedding / hybrid search ─────────────────────────────────────────────

    /// <summary>Duration of embedding generation calls (query or document).</summary>
    public static readonly Histogram<double> EmbeddingDuration =
        Meter.CreateHistogram<double>(
            "rockbot.embedding.duration",
            unit: "ms",
            description: "Duration of text-embedding generation calls");

    /// <summary>Total embedding generation calls.</summary>
    public static readonly Counter<long> EmbeddingCalls =
        Meter.CreateCounter<long>(
            "rockbot.embedding.calls",
            unit: "{call}",
            description: "Total text-embedding generation calls");

    /// <summary>Embedding generation failures (timeout, network, model error).</summary>
    public static readonly Counter<long> EmbeddingFailures =
        Meter.CreateCounter<long>(
            "rockbot.embedding.failures",
            unit: "{failure}",
            description: "Text-embedding generation failures");

    /// <summary>Duration of hybrid search (BM25 + vector ranking combined).</summary>
    public static readonly Histogram<double> HybridSearchDuration =
        Meter.CreateHistogram<double>(
            "rockbot.search.hybrid.duration",
            unit: "ms",
            description: "Duration of hybrid BM25+vector search operations");

    // ── FinOps ────────────────────────────────────────────────────────────────

    /// <summary>Estimated USD cost per LLM call, labelled by model and tier.</summary>
    public static readonly Counter<double> LlmCostUsd =
        Meter.CreateCounter<double>(
            "rockbot.llm.cost.usd",
            unit: "{USD}",
            description: "Estimated USD cost of LLM calls based on published token pricing");

    /// <summary>
    /// Per-request USD cost as a histogram. Supports exemplars (trace_id linkage) and
    /// distribution analysis. Use this to click a cost spike in Grafana and jump to the
    /// trace that caused it.
    /// </summary>
    public static readonly Histogram<double> LlmCostPerRequest =
        Meter.CreateHistogram<double>(
            "rockbot.llm.cost.per_request",
            unit: "{USD}",
            description: "USD cost per individual LLM request — histogram enables exemplar trace linkage");

    // ── WIP tracking ─────────────────────────────────────────────────────────

    /// <summary>WIP entries created (message received, persisted to disk).</summary>
    public static readonly Counter<long> WipBegun =
        Meter.CreateCounter<long>(
            "rockbot.wip.begun",
            unit: "{entry}",
            description: "WIP entries created");

    /// <summary>WIP entries completed (processing finished normally).</summary>
    public static readonly Counter<long> WipCompleted =
        Meter.CreateCounter<long>(
            "rockbot.wip.completed",
            unit: "{entry}",
            description: "WIP entries completed");

    /// <summary>WIP entries recovered on startup (replayed after crash).</summary>
    public static readonly Counter<long> WipRecovered =
        Meter.CreateCounter<long>(
            "rockbot.wip.recovered",
            unit: "{entry}",
            description: "WIP entries recovered on startup");

    /// <summary>WIP entries abandoned as stale (too old to recover).</summary>
    public static readonly Counter<long> WipAbandoned =
        Meter.CreateCounter<long>(
            "rockbot.wip.abandoned",
            unit: "{entry}",
            description: "WIP entries abandoned as stale");
}
