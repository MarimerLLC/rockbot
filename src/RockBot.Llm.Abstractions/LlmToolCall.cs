namespace RockBot.Llm;

/// <summary>
/// A tool call returned by the model.
/// </summary>
public sealed record LlmToolCall
{
    /// <summary>
    /// Unique ID for this tool call.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Name of the tool to invoke.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// JSON string of the arguments to pass to the tool.
    /// </summary>
    public string? Arguments { get; init; }
}
