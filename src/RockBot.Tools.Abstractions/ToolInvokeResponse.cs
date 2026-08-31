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
    /// Tool output content (text), or null if the tool produced no output.
    /// For rich results (images, audio, etc.) see <see cref="ContentBlocks"/>.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Structured content blocks from the tool result. When present, preserves
    /// non-text blocks (images, audio, etc.) that cannot be represented in
    /// <see cref="Content"/>. Consumers that can handle rich content should
    /// prefer this over <see cref="Content"/>.
    /// </summary>
    public IReadOnlyList<ToolContentBlock>? ContentBlocks { get; init; }

    /// <summary>
    /// Whether the tool execution resulted in an error.
    /// </summary>
    public bool IsError { get; init; }
}
