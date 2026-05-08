namespace RockBot.Observation;

/// <summary>
/// Differential evaluator: given a target's eligible candidates and its existing
/// theories, asks the LLM to verdict each candidate (promote/refine/reject).
/// Implementations are expected to call the target's configured evaluation tier
/// LLM client. Routine failures (timeouts, malformed JSON) should be caught
/// and surfaced as an empty verdict list so the surrounding phase can skip
/// promotion this dream and try again on the next.
/// </summary>
public interface IObservationEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="eligibleCandidates"/> against
    /// <paramref name="existingTheories"/> for one target. Returns at most
    /// one verdict per input candidate; candidates without a recognised
    /// verdict are treated as <see cref="EvaluationAction.Unspecified"/>.
    /// MUST throw on cancellation.
    /// </summary>
    Task<IReadOnlyList<EvaluationVerdict>> EvaluateAsync(
        ObservationTarget target,
        IReadOnlyList<Candidate> eligibleCandidates,
        IReadOnlyList<Theory> existingTheories,
        CancellationToken cancellationToken);
}
