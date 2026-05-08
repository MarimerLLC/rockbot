namespace RockBot.Observation;

/// <summary>
/// Per-conversation observation extractor. Calls the target's configured
/// extraction LLM (typically Low-tier) with the target's prompt and the
/// formatted transcript turns from one conversation; returns proposed
/// observations with quote citations. Returned observations are NOT yet
/// quote-validated — that filtering happens in a separate step.
/// </summary>
public interface IObservationExtractor
{
    /// <summary>
    /// Extracts proposed observations from one conversation. Returns an empty
    /// list if the LLM produces nothing usable. Implementations should not
    /// throw on routine LLM failures (malformed JSON, content filter, etc.) —
    /// log and return empty so the surrounding pipeline can apply
    /// "skip and continue" semantics for one bad conversation in a batch.
    /// Implementations MUST throw on cancellation.
    /// </summary>
    Task<IReadOnlyList<ProposedObservation>> ExtractAsync(
        ObservationTarget target,
        IReadOnlyList<TranscriptTurn> conversationTurns,
        CancellationToken cancellationToken);
}
