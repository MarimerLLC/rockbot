using System.Text.Json.Serialization;

namespace RockBot.Wisp;

/// <summary>
/// How a wisp step is executed.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepMode>))]
public enum StepMode
{
    /// <summary>
    /// Harness calls the tool with exact parameters. Zero LLM tokens.
    /// </summary>
    Direct,

    /// <summary>
    /// Wisp LLM interprets the step prompt and makes tool calls with minimal context.
    /// </summary>
    Llm
}
