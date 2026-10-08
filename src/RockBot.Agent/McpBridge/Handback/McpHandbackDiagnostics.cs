using System.Diagnostics;
using System.Diagnostics.Metrics;
using RockBot.Tools;

namespace RockBot.Agent.McpBridge.Handback;

/// <summary>
/// Metrics for MCP elicitation hand-back, on the shared <c>RockBot.Tools</c> meter. Never tagged
/// with anything from an answer.
/// </summary>
internal static class McpHandbackDiagnostics
{
    private static readonly Counter<long> Questions =
        ToolDiagnostics.Meter.CreateCounter<long>(
            "rockbot.mcp.handback.questions",
            unit: "{question}",
            description: "MCP questions handed back to the calling agent, and what became of them. " +
                         "Tags: server, outcome (handed_back|answered|declined|expired).");

    private static readonly Counter<long> Rejections =
        ToolDiagnostics.Meter.CreateCounter<long>(
            "rockbot.mcp.handback.rejections",
            unit: "{question}",
            description: "Questions not handed back, and mcp_answer calls refused. Tags: reason.");

    private static readonly Counter<long> Interruptions =
        ToolDiagnostics.Meter.CreateCounter<long>(
            "rockbot.mcp.handback.interruptions",
            unit: "{question}",
            description: "Handed-back questions whose call a restart interrupted. Tags: outcome (interrupted|notified).");

    private static Func<int>? _parked;

    static McpHandbackDiagnostics()
    {
        ToolDiagnostics.Meter.CreateObservableGauge(
            "rockbot.mcp.handback.parked_calls",
            () => _parked?.Invoke() ?? 0,
            unit: "{call}",
            description: "MCP tool calls parked while their question waits for an answer.");
    }

    /// <summary>Points the parked-calls gauge at the bridge's store.</summary>
    public static void ObserveParked(Func<int> count) => _parked = count;

    public static void HandedBack(string server) => Record(server, "handed_back");

    public static void Answered(string server) => Record(server, "answered");

    public static void DeclinedByAgent(string server) => Record(server, "declined");

    public static void Expired(string server) => Record(server, "expired");

    public static void Rejected(string reason) =>
        Rejections.Add(1, new TagList { { "reason", reason } });

    public static void Interrupted(int count) =>
        Interruptions.Add(count, new TagList { { "outcome", "interrupted" } });

    public static void Notified() =>
        Interruptions.Add(1, new TagList { { "outcome", "notified" } });

    private static void Record(string server, string outcome) =>
        Questions.Add(1, new TagList { { "server", server }, { "outcome", outcome } });
}
