namespace RockBot.Observation;

/// <summary>
/// Phase 2 of the observation pipeline: differential evaluation, promotion,
/// aging, markdown regeneration, and snapshot append. Reads the JSON state
/// updated by phase 1, applies all the deterministic bookkeeping plus one
/// LLM evaluation pass for any candidates that crossed the promotion
/// threshold, and writes the new state and regenerated markdown atomically.
/// </summary>
/// <remarks>
/// Atomicity contract per <c>design/observation-framework.md</c>: the JSON
/// state file and the markdown file are both rewritten only after the
/// pipeline produces a complete, consistent result. Cancellation before the
/// final writes leaves both files at their pre-phase contents.
/// </remarks>
public interface IObservationEvaluationPhase
{
    /// <summary>
    /// Runs phase 2 for one target. Returns a summary of what was done.
    /// </summary>
    Task<EvaluationPhaseResult> ExecuteAsync(
        ObservationTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// Summary of a phase 2 execution for one target.
/// </summary>
/// <param name="CandidatesAged">Candidates dropped because they had no new references within the candidate-aging window.</param>
/// <param name="TheoriesAged">Theories dropped because they had no new supporting references within the theory-aging window.</param>
/// <param name="CandidatesEvaluated">Candidates that crossed the promotion threshold and were submitted to the evaluation LLM.</param>
/// <param name="CandidatesPromoted">Candidates whose verdict was Promote and were converted into theories.</param>
/// <param name="CandidatesRefined">Candidates whose verdict was Refine; their text was updated, they remain candidates.</param>
/// <param name="CandidatesRejected">Candidates whose verdict was Reject and were removed from the pool.</param>
/// <param name="MarkdownRegenerated">Whether the markdown output file was rewritten this run. False indicates a no-op (e.g. no state file found).</param>
/// <param name="StateWritten">Whether the JSON state file was rewritten this run.</param>
public sealed record EvaluationPhaseResult(
    int CandidatesAged,
    int TheoriesAged,
    int CandidatesEvaluated,
    int CandidatesPromoted,
    int CandidatesRefined,
    int CandidatesRejected,
    bool MarkdownRegenerated,
    bool StateWritten);
