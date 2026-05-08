namespace RockBot.Observation;

/// <summary>
/// Per-candidate verdict from the differential evaluation pass.
/// </summary>
public enum EvaluationAction
{
    /// <summary>
    /// Default when the LLM does not produce a recognised verdict for a
    /// candidate. The framework treats this as "do nothing" — the candidate
    /// is left in place and remains eligible for promotion next time it is
    /// reinforced.
    /// </summary>
    Unspecified = 0,

    /// <summary>
    /// Candidate is grounded, distinct from existing theories, and ready
    /// to graduate. Phase 2 turns it into a <see cref="Theory"/> and removes
    /// it from the candidate pool.
    /// </summary>
    Promote = 1,

    /// <summary>
    /// Candidate captures something real but its text needs adjustment.
    /// Phase 2 updates the candidate's text using <see cref="EvaluationVerdict.RefinedText"/>
    /// but leaves it in the candidate pool to accumulate more evidence
    /// before promotion.
    /// </summary>
    Refine = 2,

    /// <summary>
    /// Candidate is not grounded, conflicts with existing theories, or is
    /// otherwise too noisy to keep. Phase 2 removes it from the pool.
    /// </summary>
    Reject = 3,
}

/// <summary>
/// One verdict produced by the differential evaluation pass for one candidate
/// that crossed the promotion threshold. Verdicts are applied deterministically
/// by phase 2 — the LLM proposes, the framework disposes.
/// </summary>
/// <param name="CandidateId">ID of the candidate this verdict applies to.</param>
/// <param name="Action">What to do with the candidate.</param>
/// <param name="RefinedText">When <see cref="Action"/> is <see cref="EvaluationAction.Refine"/> or <see cref="EvaluationAction.Promote"/>, this carries the LLM's preferred wording. Null otherwise.</param>
/// <param name="Reason">Optional rationale from the LLM. Logged for diagnostics; not persisted in state.</param>
public sealed record EvaluationVerdict(
    string CandidateId,
    EvaluationAction Action,
    string? RefinedText,
    string? Reason);
