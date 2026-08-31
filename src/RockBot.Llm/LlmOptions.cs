namespace RockBot.Llm;

/// <summary>
/// Configuration options for the LLM handler.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>
    /// Default model ID used when the request doesn't specify one.
    /// </summary>
    public string? DefaultModelId { get; set; }

    /// <summary>
    /// Default temperature used when the request doesn't specify one.
    /// </summary>
    public float? DefaultTemperature { get; set; }

    /// <summary>
    /// Default topic for publishing responses when no ReplyTo is set.
    /// </summary>
    public string DefaultResponseTopic { get; set; } = "llm.response";
}
