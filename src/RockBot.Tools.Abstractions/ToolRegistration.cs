using RockBot.Llm;

namespace RockBot.Tools;

/// <summary>
/// Metadata for a registered tool. Bridges tool registration to LLM tool definitions.
/// </summary>
public sealed record ToolRegistration
{
    /// <summary>
    /// Unique name of the tool.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of what the tool does.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// JSON Schema string describing the tool's parameters, or null if none.
    /// </summary>
    public string? ParametersSchema { get; init; }

    /// <summary>
    /// Source backend that provides this tool (e.g. "mcp", "rest", "script").
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Convert to an <see cref="LlmToolDefinition"/> for passing to the LLM.
    /// </summary>
    public LlmToolDefinition ToLlmToolDefinition() => new()
    {
        Name = Name,
        Description = Description,
        ParametersSchema = ParametersSchema
    };
}
