using System.Text.Json;

namespace RockBot.Agent.McpBridge.ArgGuards;

/// <summary>
/// One <c>argGuards</c> entry from a server's mcp.json config. Deserialized with the
/// bridge's camelCase/case-insensitive options.
/// </summary>
public sealed class McpArgGuardConfig
{
    /// <summary>
    /// Registry name of the <see cref="IMcpArgGuard"/> implementation, e.g. "path-prefix".
    /// An unknown name refuses the server connection (fail closed).
    /// </summary>
    public string? Handler { get; set; }

    /// <summary>
    /// Tool names this rule applies to (case-insensitive). Empty = all tools on the server.
    /// </summary>
    public List<string> Tools { get; set; } = [];

    /// <summary>
    /// Handler-specific options, kept as raw JSON and bound by the handler.
    /// <see cref="JsonElement"/> round-trips losslessly through config persistence,
    /// so guard options survive the bridge's seed/dedup rewrites of mcp.json.
    /// </summary>
    public JsonElement? Options { get; set; }
}
