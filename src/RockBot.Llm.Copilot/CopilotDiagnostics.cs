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
            description: "Copilot SendAsync calls");

    public static readonly Counter<long> RequestsRateLimited =
        Meter.CreateCounter<long>(
            "rockbot.copilot.requests.rate_limited",
            unit: "{retry}",
            description: "Copilot rate-limit retries");

    public static readonly Histogram<double> RequestDuration =
        Meter.CreateHistogram<double>(
            "rockbot.copilot.request.duration",
            unit: "ms",
            description: "Per-request Copilot latency");
}
