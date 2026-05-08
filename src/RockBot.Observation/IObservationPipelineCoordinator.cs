namespace RockBot.Observation;

/// <summary>
/// One-shot driver that runs the observation pipeline for every registered
/// <see cref="ObservationTarget"/> against a single batch of transcripts.
/// Encapsulates the per-target loop so the dream cycle's hook is a single
/// call.
/// </summary>
public interface IObservationPipelineCoordinator
{
    /// <summary>
    /// For each registered target: applies the target's
    /// <see cref="ITranscriptFilter"/>, runs phase 1 (extraction + merge),
    /// and runs phase 2 (evaluation + promotion + aging + regeneration +
    /// memory publish). Returns one <see cref="ObservationTargetRunResult"/>
    /// per target — including for targets where the pipeline failed, so the
    /// caller can log per-target outcomes.
    /// </summary>
    /// <remarks>
    /// Each target runs independently. A failure for one target is logged
    /// and does not prevent other targets from running. Cancellation is
    /// honored across the whole loop — when <paramref name="cancellationToken"/>
    /// fires, in-flight targets abort and remaining targets are not started.
    /// </remarks>
    Task<IReadOnlyList<ObservationTargetRunResult>> RunAllAsync(
        IReadOnlyList<TranscriptTurn> transcripts,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-target outcome from <see cref="IObservationPipelineCoordinator.RunAllAsync"/>.
/// </summary>
/// <param name="TargetName">The target whose pipeline ran.</param>
/// <param name="ExtractionResult">Phase 1 result, or <c>null</c> if phase 1 was not reached (e.g. the target threw before phase 1).</param>
/// <param name="EvaluationResult">Phase 2 result, or <c>null</c> if phase 2 was not reached.</param>
/// <param name="Failure">Captured exception when the target's pipeline threw an unhandled exception. <c>null</c> on success.</param>
public sealed record ObservationTargetRunResult(
    string TargetName,
    ExtractionPhaseResult? ExtractionResult,
    EvaluationPhaseResult? EvaluationResult,
    Exception? Failure);
