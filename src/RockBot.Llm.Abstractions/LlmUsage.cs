namespace RockBot.Llm;

/// <summary>
/// Token usage statistics from a model invocation.
/// </summary>
public sealed record LlmUsage
{
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens { get; init; }
}
