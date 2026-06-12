using System.Text.Json;

namespace RockBot.Agent.McpBridge.ArgGuards;

/// <summary>
/// Everything a guard needs to evaluate one invocation. <paramref name="Arguments"/> is
/// the same instance the bridge forwards to the MCP server (mutable by design);
/// <paramref name="Options"/> is the raw per-rule JSON from mcp.json, bound by each handler.
/// </summary>
public sealed record McpArgGuardContext(
    string ServerName,
    string ToolName,
    Dictionary<string, object?> Arguments,
    JsonElement? Options);
