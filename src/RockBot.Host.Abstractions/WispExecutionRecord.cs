namespace RockBot.Host;

/// <summary>
/// Persistent record of a single wisp execution, written after every run (success or failure).
/// Queried by the dream system to detect recurring failure patterns and skill improvement candidates.
/// </summary>
public sealed record WispExecutionRecord
{
    /// <summary>Unique identifier for this wisp execution.</summary>
    public required string WispId { get; init; }

    /// <summary>Description from the wisp definition.</summary>
    public required string Description { get; init; }

    /// <summary>SHA-256 hash of the serialized step definitions for matching retries.</summary>
    public required string DefinitionHash { get; init; }

    /// <summary>Whether all steps completed successfully.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Total number of steps in the definition.</summary>
    public required int StepCount { get; init; }

    /// <summary>Number of steps that executed successfully.</summary>
    public required int StepsCompleted { get; init; }

    /// <summary>Step ID that caused failure, if any.</summary>
    public string? FailedStepId { get; init; }

    /// <summary>Step index that caused failure, if any.</summary>
    public int? FailedStepIndex { get; init; }

    /// <summary>Failure classification for the failed step.</summary>
    public string? FailureCategory { get; init; }

    /// <summary>Error message from the failed step.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Tool name that failed, if applicable.</summary>
    public string? FailedToolName { get; init; }

    /// <summary>Total execution duration in milliseconds.</summary>
    public required int DurationMs { get; init; }

    /// <summary>When the wisp execution occurred.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Session ID of the calling agent, for correlating retries.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// WispId of a prior failed execution that this run corrects, if detected as a retry.
    /// </summary>
    public string? RetryOf { get; init; }

    /// <summary>
    /// Batch identifier for correlating wisps executed together in a single spawn_wisps call.
    /// Null for single-wisp executions or legacy records.
    /// </summary>
    public string? BatchId { get; init; }

    /// <summary>
    /// JSON-serialized step definitions for this run, retained when the run succeeded
    /// and the body fits the size cap so promotion to a skill resource is possible.
    /// Null for failed runs, oversize runs (see <see cref="BodyOmittedTooLarge"/>),
    /// and pre-existing records from before this field was added.
    /// </summary>
    public string? DefinitionBody { get; init; }

    /// <summary>
    /// True when <see cref="DefinitionBody"/> would have been retained but the
    /// serialized body exceeded the per-record cap. Promotion passes treat such
    /// records as ineligible.
    /// </summary>
    public bool BodyOmittedTooLarge { get; init; }
}
