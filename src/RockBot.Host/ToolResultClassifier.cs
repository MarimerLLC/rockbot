namespace RockBot.Host;

/// <summary>
/// Classifies a completed tool call's result string as an error or a timeout. The one place the
/// agent loop (native and text-based paths) and <see cref="RockBotFunctionInvokingChatClient"/>
/// decide this, so tier escalation, the consecutive-timeout abort, tool metrics and the
/// <see cref="LoopToolCallLedger"/> all agree.
/// </summary>
internal static class ToolResultClassifier
{
    private const string ErrorPrefix = "Error: ";

    /// <summary>
    /// True when the tool result indicates a logical (content-level) error rather than a
    /// transport success. <c>RegistryToolFunction</c> prepends "Error: " to any
    /// response with <c>IsError=true</c>, so this prefix reliably signals "the executor
    /// completed but the operation failed" — including MCP server errors propagated by
    /// <c>McpToolProxy</c>.
    /// </summary>
    public static bool IsError(string result) =>
        result.StartsWith(ErrorPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when the tool call timed out. Only RockBot's error envelope counts (#762): a
    /// successful result whose content quotes "timed out" — a memory hit, a web page, an email,
    /// a log excerpt — is not a timeout. RockBot's own timeouts (MCP proxy and bridge, script
    /// runners, council deliberation, memory regex search) all come back as <c>Error: …</c>.
    /// </summary>
    public static bool IsTimeout(string result) =>
        IsError(result) && result.Contains("timed out", StringComparison.OrdinalIgnoreCase);
}
