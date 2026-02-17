namespace RockBot.Llm;

/// <summary>
/// Serialization-safe chat message for transport over the message bus.
/// Uses simple types instead of M.E.AI's polymorphic ChatMessage.
/// </summary>
public sealed record LlmChatMessage
{
    /// <summary>
    /// Role of the message author (e.g. "system", "user", "assistant", "tool").
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Text content of the message.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Tool calls made by the assistant in this message.
    /// </summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// The tool call ID this message is responding to (for tool role messages).
    /// </summary>
    public string? ToolCallId { get; init; }
}
