namespace RockBot.Tools;

/// <summary>
/// Configuration options for the tool handler.
/// </summary>
public sealed class ToolOptions
{
    /// <summary>
    /// Default topic for publishing tool results when no ReplyTo is set.
    /// </summary>
    public string DefaultResultTopic { get; set; } = "tool.result";
}
