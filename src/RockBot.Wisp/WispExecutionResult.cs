using RockBot.Host;

namespace RockBot.Wisp;

/// <summary>
/// Result of executing a complete wisp pipeline.
/// </summary>
public sealed record WispExecutionResult
{
    /// <summary>
    /// Unique identifier for this wisp execution.
    /// </summary>
    public required string WispId { get; init; }

    /// <summary>
    /// Whether all steps completed successfully.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Per-step results in execution order.
    /// </summary>
    public required IReadOnlyList<WispStepResult> StepResults { get; init; }

    /// <summary>
    /// Total execution duration.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// The step that caused failure, if any.
    /// </summary>
    public WispStepResult? FailedStep => StepResults.FirstOrDefault(s => !s.IsSuccess);

    /// <summary>Every tool call the wisp's steps made, in order (#686).</summary>
    public IReadOnlyList<LoopToolCall> ToolCalls =>
        StepResults.SelectMany(s => s.ToolCalls ?? []).ToList();

    /// <summary>
    /// The original wisp definition for provenance tracking.
    /// </summary>
    public required WispDefinition Definition { get; init; }
}
