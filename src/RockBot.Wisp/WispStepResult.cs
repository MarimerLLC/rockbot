using RockBot.Host;

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

    /// <summary>
    /// The tool calls this step made (#686): the one call of a direct step, every substantive call
    /// of an LLM step's loop. Null when the step made none (a validation failure, a skipped step).
    /// They are reported on the parent's <c>spawn_wisps</c> call so a claim about the wisps' work
    /// can be checked against what actually ran.
    /// </summary>
    public IReadOnlyList<LoopToolCall>? ToolCalls { get; init; }
}
