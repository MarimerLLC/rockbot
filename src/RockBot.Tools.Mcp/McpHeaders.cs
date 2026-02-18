namespace RockBot.Tools.Mcp;

/// <summary>
/// Well-known MCP-specific message headers used by the bridge and agent proxy.
/// </summary>
public static class McpHeaders
{
    /// <summary>
    /// When present on a <c>tool.invoke.mcp</c> message, the bridge routes the
    /// invocation to this specific server directly — bypassing the tool-name
    /// search loop.  Value is the server name (key in mcp.json).
    /// </summary>
    public const string ServerName = "rb-mcp-server";
}
