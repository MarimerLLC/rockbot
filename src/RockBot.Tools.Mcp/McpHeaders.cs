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

    /// <summary>
    /// Set to <see cref="HandbackCapable"/> on a <c>tool.invoke.mcp</c> message when the calling
    /// run can answer a handed-back question with <c>mcp_answer</c>. Without it, a server in
    /// hand-back mode gets its responder instead (see <c>design/mcp-elicitation-handback.md</c>).
    /// </summary>
    public const string Handback = "rb-mcp-handback";

    /// <summary>The value of <see cref="Handback"/> that marks a caller able to answer.</summary>
    public const string HandbackCapable = "1";
}
