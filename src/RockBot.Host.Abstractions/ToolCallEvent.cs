namespace RockBot.Host;

/// <summary>
/// Records a single tool invocation within an agent session.
/// Aggregated by the dream system to detect repeated action sequences
/// and synthesize them into reusable skills.
/// </summary>
/// <param name="SessionId">The session this tool call belongs to.</param>
/// <param name="ToolName">The name of the tool that was invoked.</param>
/// <param name="ArgumentsSummary">Summarized arguments (key=value pairs), or null if none.</param>
/// <param name="Succeeded">Whether the tool call completed successfully.</param>
/// <param name="DurationMs">Execution time in milliseconds.</param>
/// <param name="Timestamp">When the tool call occurred.</param>
public sealed record ToolCallEvent(
    string SessionId,
    string ToolName,
    string? ArgumentsSummary,
    bool Succeeded,
    int DurationMs,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Failure classification when <see cref="Succeeded"/> is <c>false</c>.
    /// Null for successful calls. Used by the dream system to identify recurring
    /// failure patterns across sessions.
    /// </summary>
    public ToolCallFailureCategory? FailureCategory { get; init; }

    /// <summary>
    /// Error message when the tool call failed. Null for successful calls.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
