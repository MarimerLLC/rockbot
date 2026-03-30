namespace RockBot.Host;

/// <summary>
/// Configuration for the file-based tool-call log.
/// </summary>
public sealed class ToolCallLogOptions
{
    /// <summary>
    /// Path for per-session tool-call JSONL files, relative to the agent profile base path.
    /// Defaults to <c>"tool-call-log"</c>.
    /// </summary>
    public string BasePath { get; set; } = "tool-call-log";
}
