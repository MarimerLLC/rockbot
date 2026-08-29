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

    // ── Histogram bucket boundaries ───────────────────────────────────────────
    // The OTel SDK's default boundaries stop at 10,000. That ceiling is one to
    // two orders of magnitude below every token count recorded here, and it is
    // 10 seconds for anything measured in milliseconds. With no explicit
    // boundaries the observations pile into the +Inf bucket, and Prometheus's
    // histogram_quantile then returns the highest FINITE bound — so p50/p95/p99
    // all render as a flat 10,000 line in Grafana no matter what was measured.
    //
    // Measured over 7d on 2026-08-28 (share of observations above 10,000):
    //   agent.context.tokens          100%   mean     19,755 tokens
    //   agent.turn.tokens.input        99%   mean    296,268 tokens
    //   agent.turn.tokens.input.cached 96%   mean    216,499 tokens
    //   agent.llm.context.tokens       93%   mean     25,509 tokens
    //   subagent.duration             100%   mean    422,590 ms
    //   agent.turn.duration            41%   mean     11,394 ms
    //   llm.request.duration           39%   mean     36,449 ms
    //   embedding.duration             29%   mean      6,458 ms
    //   pipeline.dispatch.duration     20%   mean     21,592 ms
    //
    // Instruments left on the SDK defaults (turn.tokens.output, turn.tools, and
    // the sub-second duration metrics) measured fully in range, so they keep the
    // defaults rather than churn a working metric.
    //
    // These must be declared BEFORE the instruments that reference them —
    // static field initializers run in declaration order, so moving them down
    // the file would hand CreateHistogram a null boundary list.

    /// <summary>
    /// Buckets for per-turn cumulative token counts. These sum across every
    /// internal LLM call in a turn, so a long tool loop reaches the millions.
    /// </summary>
    private static readonly IReadOnlyList<long> TurnTokenBuckets =
        [1_000, 5_000, 10_000, 25_000, 50_000, 100_000,
         250_000, 500_000, 1_000_000, 2_500_000, 5_000_000];

    /// <summary>
    /// Buckets for single-payload context sizes. Bounded by the model's context
    /// window rather than by loop length, hence a tighter top end than
    /// <see cref="TurnTokenBuckets"/>.
    /// </summary>
    private static readonly IReadOnlyList<long> ContextTokenBuckets =
        [1_000, 2_500, 5_000, 10_000, 25_000, 50_000,
         100_000, 200_000, 400_000, 1_000_000];

    /// <summary>
    /// Buckets (ms) for request-scoped work: one call out to a model or embedding
    /// endpoint, or a single pass through the dispatch pipeline. Spans 10ms to
    /// 10 minutes — the top end covers slow generations, which do exceed a minute.
    /// </summary>
    public static readonly IReadOnlyList<double> RequestDurationBuckets =
        [10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000,
         10_000, 30_000, 60_000, 120_000, 300_000, 600_000];

    /// <summary>
    /// Buckets (ms) for whole-turn and subagent work, which drives an entire tool
    /// loop and routinely runs for minutes. Shared with
    /// <c>RockBot.Subagent.SubagentDiagnostics</c> so both read on one scale.
    /// </summary>
    public static readonly IReadOnlyList<double> LoopDurationBuckets =
        [500, 1_000, 2_500, 5_000, 10_000, 30_000, 60_000,
         120_000, 300_000, 600_000, 1_200_000, 1_800_000, 3_600_000];

    public static readonly Histogram<double> DispatchDuration =
        Meter.CreateHistogram<double>(
            "rockbot.pipeline.dispatch.duration",
            unit: "ms",
            description: "Duration of message dispatch through the pipeline",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = RequestDurationBuckets });

    // ── LLM metrics — recorded in LlmClient (the actual call path) ───────────
    public static readonly Histogram<double> LlmRequestDuration =
        Meter.CreateHistogram<double>(
            "rockbot.llm.request.duration",
            unit: "ms",
            description: "Duration of LLM request operations",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = RequestDurationBuckets });

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
    /// Subset of <see cref="LlmTokenInput"/> that the provider served from its prompt
    /// cache. Read from <c>Usage.AdditionalCounts["InputTokenDetails.CachedTokenCount"]</c>
    /// when present (OpenAI/Azure Foundry surface this; other providers may not). A high
    /// ratio of cached/input tokens indicates a stable prefix across calls.
    /// </summary>
    public static readonly Counter<long> LlmTokenInputCached =
        Meter.CreateCounter<long>(
            "rockbot.llm.token.input.cached",
            unit: "{token}",
            description: "Input tokens served from provider-side prompt cache (subset of LlmTokenInput)");

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
    /// Number of LLM calls that were rejected immediately because the per-tier
    /// gateway queue had reached its bounded depth
    /// (<c>MaxConcurrent + MaxPending</c>). Tagged by tier. A non-zero rate is
    /// a strong signal of either a runaway loop submitting work or sustained
    /// upstream rate limiting; investigate before raising the caps.
    /// </summary>
    public static readonly Counter<long> LlmGatewaySaturationRejections =
        Meter.CreateCounter<long>(
            "rockbot.llm.gateway.saturation_rejections",
            unit: "{rejection}",
            description: "LLM calls rejected because the gateway queue was full");

    // ── Agent turn metrics — recorded at architectural boundaries ─────────────

    /// <summary>Duration from user message receipt to final reply published.</summary>
    public static readonly Histogram<double> TurnDuration =
        Meter.CreateHistogram<double>(
            "rockbot.agent.turn.duration",
            unit: "ms",
            description: "Duration of agent turns from message receipt to final reply",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LoopDurationBuckets });

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
            description: "Estimated token count of context injected before LLM call",
            tags: null,
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = ContextTokenBuckets });

    /// <summary>Total input tokens consumed per turn (aggregated across all LLM calls).</summary>
    public static readonly Histogram<long> TurnTokensInput =
        Meter.CreateHistogram<long>(
            "rockbot.agent.turn.tokens.input",
            unit: "{token}",
            description: "Input tokens per turn, aggregated across all LLM calls in the loop",
            tags: null,
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = TurnTokenBuckets });

    /// <summary>Total output tokens produced per turn (aggregated across all LLM calls).</summary>
    public static readonly Histogram<long> TurnTokensOutput =
        Meter.CreateHistogram<long>(
            "rockbot.agent.turn.tokens.output",
            unit: "{token}",
            description: "Output tokens per turn, aggregated across all LLM calls in the loop");

    /// <summary>Cached-input-token subset of <see cref="TurnTokensInput"/> per turn.</summary>
    public static readonly Histogram<long> TurnTokensInputCached =
        Meter.CreateHistogram<long>(
            "rockbot.agent.turn.tokens.input.cached",
            unit: "{token}",
            description: "Cached input tokens per turn (subset of TurnTokensInput) — indicates prompt-cache effectiveness",
            tags: null,
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = TurnTokenBuckets });

    /// <summary>Number of tool calls executed per turn.</summary>
    public static readonly Histogram<long> TurnToolCalls =
        Meter.CreateHistogram<long>(
            "rockbot.agent.turn.tools",
            unit: "{call}",
            description: "Tool calls executed per turn");

    /// <summary>
    /// Estimated context size (in tokens) sent at each LLM call boundary — per call,
    /// not per turn. <see cref="TurnTokensInput"/> sums across every internal FICC
    /// iteration in a turn and so doesn't reflect the size of any individual API call;
    /// this histogram does. Tagged with <c>rockbot.session.kind</c>
    /// (session/patrol/subagent/worker) so Grafana can split peak-per-call by workload.
    /// </summary>
    public static readonly Histogram<long> LlmCallContextTokens =
        Meter.CreateHistogram<long>(
            "rockbot.agent.llm.context.tokens",
            unit: "{token}",
            description: "Estimated context size at each LLM call boundary (per-call, not per-turn)",
            tags: null,
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = ContextTokenBuckets });

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
            description: "Duration of text-embedding generation calls",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = RequestDurationBuckets });

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
