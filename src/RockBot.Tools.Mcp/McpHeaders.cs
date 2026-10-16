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

    /// <summary>
    /// Set to <see cref="TasksCapable"/> on a <c>tool.invoke.mcp</c> message when the calling run
    /// can take the result in a later turn. Only then does a call to a server with
    /// <c>"tasks": true</c> run as a background task; without it the call is synchronous.
    /// </summary>
    public const string Tasks = "rb-mcp-tasks";

    /// <summary>The value of <see cref="Tasks"/> that marks a caller able to take a later result.</summary>
    public const string TasksCapable = "1";

    /// <summary>
    /// On a call that may run as a background task: the calling run's origin for the
    /// consequential-action gate (#685), as <c>ActionGateScope.LogName</c> writes it. The task's
    /// result is answered later under the same authorization the request had.
    /// </summary>
    public const string RunOrigin = "rb-run-origin";

    /// <summary>
    /// With <see cref="RunOrigin"/>: whether the originating user request asked for anything, as
    /// <c>CompletionEvalTriggers.LogName</c> writes it.
    /// </summary>
    public const string UserAskedFor = "rb-user-asked-for";
}
