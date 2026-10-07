namespace RockBot.Tools.Mcp;

/// <summary>
/// Names of typed MCP wrapper tools: <c>{server}__{tool}</c>.
/// <para>
/// Each part is sanitised to the strictest charset the LLM providers accept for tool names —
/// <c>^[a-zA-Z0-9_-]{1,64}$</c> for Anthropic and OpenAI. That is narrower than the MCP spec,
/// which also allows <c>.</c>, so <c>.</c> becomes <c>-</c> here. Names are never truncated:
/// truncation collides silently, so a name over the limit is reported and the tool stays
/// reachable through <c>mcp_invoke_tool</c> instead (mcp-aggregator#42).
/// </para>
/// <para>
/// Routing never parses a wrapper name; it uses the server and tool stored with the
/// registration. <see cref="TryParse"/> is for diagnostics only.
/// </para>
/// </summary>
public static class McpWrapperNaming
{
    /// <summary>The longest tool name the LLM providers accept.</summary>
    public const int MaxLength = 64;

    /// <summary>The wrapper name for <paramref name="toolName"/> on <paramref name="serverName"/>.</summary>
    public static string For(string serverName, string toolName) =>
        Sanitize(serverName) + McpServerNames.ToolSeparator + Sanitize(toolName);

    /// <summary>True when <paramref name="name"/> fits the providers' length limit.</summary>
    public static bool FitsProviderLimit(string name) => name.Length is > 0 and <= MaxLength;

    /// <summary>Splits a wrapper name on its first separator. Diagnostics only.</summary>
    public static bool TryParse(string name, out string serverPart, out string toolPart)
    {
        var separator = name.IndexOf(McpServerNames.ToolSeparator, StringComparison.Ordinal);
        if (separator <= 0 || separator + McpServerNames.ToolSeparator.Length >= name.Length)
        {
            serverPart = toolPart = string.Empty;
            return false;
        }

        serverPart = name[..separator];
        toolPart = name[(separator + McpServerNames.ToolSeparator.Length)..];
        return true;
    }

    private static string Sanitize(string value) =>
        value.All(IsAllowed) ? value : new string(value.Select(c => IsAllowed(c) ? c : '-').ToArray());

    private static bool IsAllowed(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
