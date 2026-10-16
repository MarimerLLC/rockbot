using System.Diagnostics;
using System.Diagnostics.Metrics;
using RockBot.Tools;

namespace RockBot.Agent.McpBridge.Tasks;

/// <summary>Metrics for background MCP tasks, on the shared <c>RockBot.Tools</c> meter.</summary>
internal static class McpTaskDiagnostics
{
    private static readonly Counter<long> Tasks =
        ToolDiagnostics.Meter.CreateCounter<long>(
            "rockbot.mcp.tasks",
            unit: "{task}",
            description: "Background MCP tasks and how they ended. Tags: server, outcome " +
                         "(started|completed|failed|cancelled|lost|timed-out|resumed|refused).");

    private static readonly Counter<long> InputRequests =
        ToolDiagnostics.Meter.CreateCounter<long>(
            "rockbot.mcp.tasks.input_requests",
            unit: "{request}",
            description: "Questions background MCP tasks asked mid-run. Tags: server, action (accept|decline|cancel|unsupported).");

    private static Func<int>? _running;

    static McpTaskDiagnostics()
    {
        ToolDiagnostics.Meter.CreateObservableGauge(
            "rockbot.mcp.tasks.running",
            () => _running?.Invoke() ?? 0,
            unit: "{task}",
            description: "Background MCP tasks the bridge is polling.");
    }

    /// <summary>Points the running-tasks gauge at the bridge's registry.</summary>
    public static void ObserveRunning(Func<int> count) => _running = count;

    public static void Record(string server, string outcome) =>
        Tasks.Add(1, new TagList { { "server", server }, { "outcome", outcome } });

    public static void InputRequest(string server, string action) =>
        InputRequests.Add(1, new TagList { { "server", server }, { "action", action } });
}
