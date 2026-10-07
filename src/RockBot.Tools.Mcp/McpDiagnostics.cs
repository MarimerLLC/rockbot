using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RockBot.Tools.Mcp;

/// <summary>Which LLM-facing surface a downstream MCP call came through.</summary>
public static class McpInvocationPath
{
    /// <summary>The generic <c>mcp_invoke_tool(server_name, tool_name, arguments)</c>.</summary>
    public const string InvokeTool = "invoke_tool";

    /// <summary>A typed <c>{server}__{tool}</c> wrapper tool.</summary>
    public const string Wrapper = "wrapper";
}

/// <summary>
/// Metrics for downstream MCP calls, on the shared <c>RockBot.Tools</c> meter. Tagging each
/// call with the surface it came through lets wrapper and <c>mcp_invoke_tool</c> reliability be
/// compared directly (mcp-aggregator#42's <c>via</c> tag).
/// </summary>
internal static class McpDiagnostics
{
    private static readonly Counter<long> Invocations =
        ToolDiagnostics.Meter.CreateCounter<long>(
            "rockbot.mcp.tool.invocations",
            unit: "{call}",
            description: "Downstream MCP tool calls. Tags: server, via (invoke_tool|wrapper), outcome (ok|error).");

    public static void RecordInvocation(string server, string via, bool isError) =>
        Invocations.Add(1, new TagList
        {
            { "server", server },
            { "via", via },
            { "outcome", isError ? "error" : "ok" }
        });
}
