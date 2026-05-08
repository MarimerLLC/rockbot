namespace RockBot.Observation;

/// <summary>
/// A promoted observation that has been reinforced across enough distinct
/// conversations to graduate from candidate status. Theories are what appear
/// in the regenerated markdown's "Theories" section and are loaded into agent
/// context as part of the agent profile.
/// </summary>
public sealed class Theory
{
    /// <summary>Stable ID assigned at promotion time.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Canonical text of the theory. Established at promotion and may be
    /// refined by the evaluation pass when new supporting references arrive
    /// that better articulate the underlying claim. As with
    /// <see cref="Candidate.Text"/>, refinements are LLM-mediated through
    /// the pipeline, not direct.
    /// </summary>
    public required string Text { get; set; }

    /// <summary>When the candidate graduated into this theory.</summary>
    public DateTimeOffset PromotedAt { get; init; }

    /// <summary>
    /// Most recent reference observation timestamp from any contributing
    /// conversation (including post-promotion reinforcements). Used by the
    /// theory-aging pass: a theory with no new supporting references in the
    /// configured theory-aging window is dropped or demoted.
    /// </summary>
    public DateTimeOffset LastReinforced { get; set; }

    /// <summary>
    /// IDs of the candidate(s) that produced this theory. A theory may have
    /// more than one source candidate when the evaluation pass merges
    /// related candidates at promotion time.
    /// </summary>
    public List<string> SourceCandidateIds { get; init; } = [];

    /// <summary>
    /// Evidence accumulated across the theory's lifetime — both the references
    /// the source candidate(s) carried at promotion and any references added
    /// by post-promotion reinforcements that matched the theory's cluster.
    /// </summary>
    public List<ObservationReference> References { get; init; } = [];
}
