namespace RockBot.Wisp;

/// <summary>
/// Structured error information for a failed wisp step.
/// </summary>
public sealed record WispStepError
{
    /// <summary>
    /// Failure classification: structural, external, judgment, or data.
    /// </summary>
    public required FailureCategory Category { get; init; }

    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// The tool name that was invoked (if applicable).
    /// </summary>
    public string? ToolName { get; init; }
}
