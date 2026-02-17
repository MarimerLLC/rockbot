namespace RockBot.Llm;

/// <summary>
/// Response from an LLM invocation. Published to the reply topic.
/// </summary>
public sealed record LlmResponse
{
    /// <summary>
    /// Text content of the response.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Tool calls requested by the model.
    /// </summary>
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Reason the model stopped generating (e.g. "stop", "tool_calls", "length").
    /// </summary>
    public string? FinishReason { get; init; }

    /// <summary>
    /// Token usage statistics.
    /// </summary>
    public LlmUsage? Usage { get; init; }

    /// <summary>
    /// The model that produced this response.
    /// </summary>
    public string? ModelId { get; init; }
}
