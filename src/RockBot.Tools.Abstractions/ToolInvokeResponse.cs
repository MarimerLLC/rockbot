namespace RockBot.Tools;

/// <summary>
/// Result of a successful tool invocation. Published to the ReplyTo topic.
/// </summary>
public sealed record ToolInvokeResponse
{
    /// <summary>
    /// LLM tool call ID for correlation.
    /// </summary>
    public required string ToolCallId { get; init; }

    /// <summary>
    /// Name of the tool that was invoked.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Tool output content (JSON or text), or null if the tool produced no output.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Whether the tool execution resulted in an error.
    /// </summary>
    public bool IsError { get; init; }
}
