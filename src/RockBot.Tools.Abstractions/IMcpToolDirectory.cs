namespace RockBot.Tools;

/// <summary>
/// One downstream MCP tool as the agent currently knows it.
/// </summary>
/// <param name="ServerName">Server the tool lives on, as the bridge names it.</param>
/// <param name="ToolName">The tool's own (downstream) name.</param>
/// <param name="Wrapper">
/// The typed <c>{server}__{tool}</c> wrapper's registration, or <c>null</c> when the tool has no
/// wrapper (name over the 64-character limit, a sanitised-name collision, or every tier Off). Such a
/// tool is still callable through <c>mcp_invoke_tool</c>.
/// </param>
/// <param name="Fingerprint">
/// The bridge's fingerprint of the tool (name, description, canonical input schema), or <c>null</c>
/// when unknown.
/// </param>
public sealed record McpToolEntry(string ServerName, string ToolName, ToolRegistration? Wrapper, string? Fingerprint);

/// <summary>
/// Looks up downstream MCP tools and their typed wrappers for code that can't reference the MCP
/// gateway — wisps above all (#647). In pinned and lazy modes typed wrappers are never in the
/// <see cref="IToolRegistry"/>, so this is the only way to find one by server and tool, or by
/// typed name.
/// <para>
/// Typed names are looked up, never parsed: sanitising is lossy (<c>.</c> becomes <c>-</c>) and a
/// tool name may itself contain <c>__</c>.
/// </para>
/// </summary>
public interface IMcpToolDirectory
{
    /// <summary>
    /// Resolves a tool. With <paramref name="serverName"/>, <paramref name="tool"/> is the
    /// downstream tool name (exact match first, then case-insensitive). Without it,
    /// <paramref name="tool"/> must be a typed wrapper name. Returns <c>null</c> when the server
    /// isn't indexed, the tool isn't on it, or the typed name is unknown.
    /// </summary>
    McpToolEntry? Resolve(string? serverName, string tool);

    /// <summary>The tools of <paramref name="serverName"/> that have typed wrappers.</summary>
    IReadOnlyList<McpToolEntry> ForServer(string serverName);

    /// <summary>
    /// Whether the agent has a current index entry for <paramref name="serverName"/>. Tells "the
    /// tool disappeared" (indexed server, no such tool) apart from "can't tell" (server unknown).
    /// </summary>
    bool IsServerIndexed(string serverName);

    /// <summary>Runs any typed wrapper: pass the wrapper's name and the tool's own arguments.</summary>
    IToolExecutor WrapperExecutor { get; }
}
