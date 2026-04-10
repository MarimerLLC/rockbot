namespace RockBot.Wisp;

/// <summary>
/// Result of executing a single wisp step.
/// </summary>
public sealed record WispStepResult
{
    /// <summary>
    /// The step ID from the wisp definition.
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Zero-based index of this step in the definition.
    /// </summary>
    public required int StepIndex { get; init; }

    /// <summary>
    /// Whether this step succeeded.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// The tool response content on success, or LLM output for llm steps.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>
    /// Error details if the step failed.
    /// </summary>
    public WispStepError? Error { get; init; }

    /// <summary>
    /// Execution duration for this step.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether this step was skipped (e.g. due to on_failure skip_to).
    /// </summary>
    public bool WasSkipped { get; init; }

    /// <summary>
    /// Whether this step's failure was handled by an on_failure action (e.g. skip_to).
    /// When true, the failure does not abort the pipeline.
    /// </summary>
    public bool FailureHandled { get; init; }
}
