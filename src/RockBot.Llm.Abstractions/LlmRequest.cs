namespace RockBot.Llm;

/// <summary>
/// Request to invoke an LLM. Published to "llm.request".
/// </summary>
public sealed record LlmRequest
{
    /// <summary>
    /// The conversation messages to send to the model.
    /// </summary>
    public required IReadOnlyList<LlmChatMessage> Messages { get; init; }

    /// <summary>
    /// Optional model identifier override.
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Optional temperature (0.0–2.0).
    /// </summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Optional maximum output tokens.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Optional tool definitions for function calling.
    /// </summary>
    public IReadOnlyList<LlmToolDefinition>? Tools { get; init; }

    /// <summary>
    /// Optional stop sequences.
    /// </summary>
    public IReadOnlyList<string>? StopSequences { get; init; }
}
