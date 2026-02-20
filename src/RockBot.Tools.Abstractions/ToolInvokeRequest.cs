namespace RockBot.Tools;

/// <summary>
/// Request to invoke a tool. Published to "tool.invoke".
/// </summary>
public sealed record ToolInvokeRequest
{
    /// <summary>
    /// LLM tool call ID for correlation back to the originating response.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Name of the tool to invoke.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// JSON arguments string, or null if the tool takes no arguments.
    /// </summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Session ID of the originating conversation, used to key working memory entries.
    /// Null when invoked outside a session context (e.g. tests, CLI tools).
    /// </summary>
    public string? SessionId { get; init; }
}
