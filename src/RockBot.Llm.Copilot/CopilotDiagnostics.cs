using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Llm.Copilot;

/// <summary>
/// Centralized diagnostics for the Copilot chat client adapter.
/// Uses BCL APIs (ActivitySource + Meter) that are zero-cost when no listener is attached.
/// </summary>
internal static class CopilotDiagnostics
{
    public const string ActivitySourceName = "RockBot.Llm.Copilot";
    public const string MeterName = "RockBot.Llm.Copilot";

    public static readonly ActivitySource Source = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> SessionsCreated =
        Meter.CreateCounter<long>(
            "rockbot.copilot.sessions.created",
            unit: "{session}",
            description: "Copilot sessions created");

    public static readonly Counter<long> RequestsSent =
        Meter.CreateCounter<long>(
            "rockbot.copilot.requests.sent",
            unit: "{request}",
            description: "Copilot SendAsync calls (one per session)");

    public static readonly Counter<long> RequestsRateLimited =
        Meter.CreateCounter<long>(
            "rockbot.copilot.requests.rate_limited",
            unit: "{retry}",
            description: "Copilot rate-limit retries");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "rockbot.copilot.request.duration",
            unit: "ms",
            description: "Per-session Copilot latency (includes tool loop)");

    // ── Per-LLM-call metrics (from AssistantUsageEvent) ──────────────────────
    // Each premium interaction within a session fires an AssistantUsageEvent.
    // These track actual billing-relevant usage.

    public static readonly Counter<long> PremiumRequests =
        Meter.CreateCounter<long>(
            "rockbot.copilot.premium_requests",
            unit: "{request}",
            description: "Premium LLM interactions (actual billing events)");

    public static readonly Counter<long> TokenInput =
        Meter.CreateCounter<long>(
            "rockbot.copilot.token.input",
            unit: "{token}",
            description: "Input tokens consumed across all premium requests");

    public static readonly Counter<long> TokenOutput =
        Meter.CreateCounter<long>(
            "rockbot.copilot.token.output",
            unit: "{token}",
            description: "Output tokens produced across all premium requests");

    public static readonly Counter<double> CostMultiplier =
        Meter.CreateCounter<double>(
            "rockbot.copilot.cost.multiplier",
            description: "Cumulative model multiplier cost from premium requests");

    public static readonly Histogram<double> LlmCallDuration =
        Meter.CreateHistogram<double>(
            "rockbot.copilot.llm_call.duration",
            unit: "ms",
            description: "Per-LLM-call latency within Copilot sessions");
}
