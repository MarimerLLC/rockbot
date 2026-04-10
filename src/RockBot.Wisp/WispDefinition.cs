using System.Text.Json.Serialization;

namespace RockBot.Wisp;

/// <summary>
/// A wisp definition — a harness-native pipeline with optional LLM steps.
/// The calling agent/subagent produces this structured JSON definition specifying
/// ordered steps, tool scopes, and expected behavior.
/// </summary>
public sealed record WispDefinition
{
    /// <summary>
    /// Human-readable description of what this wisp does.
    /// </summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// Additional tools that <c>llm</c> steps need beyond what <c>direct</c> steps declare.
    /// These are tool names (e.g. "web_browse") that only LLM steps reference.
    /// </summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>
    /// Ordered sequence of steps to execute.
    /// </summary>
    [JsonPropertyName("steps")]
    public required IReadOnlyList<WispStep> Steps { get; init; }
}
