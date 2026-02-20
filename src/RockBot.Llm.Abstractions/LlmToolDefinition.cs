namespace RockBot.Llm;

/// <summary>
/// Tool schema passed to the model for function calling.
/// </summary>
public sealed record LlmToolDefinition
{
    /// <summary>
    /// Name of the tool.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what the tool does.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// JSON Schema string describing the tool's parameters.
    /// </summary>
    public string? ParametersSchema { get; init; }
}
