namespace RockBot.Observation;

/// <summary>
/// Phase 1 of the observation pipeline: per-conversation extraction,
/// quote-grounding, vector clustering, and merge into the candidate pool.
/// This is the read-many-write-once pass that updates one target's JSON
/// state with new evidence harvested from a batch of conversations.
/// </summary>
/// <remarks>
/// <para>
/// Caller responsibilities:
/// </para>
/// <list type="bullet">
///   <item>Hand in transcripts already filtered by the framework's
///   <see cref="ITranscriptFilter"/> for the target. (The phase does not
///   re-apply the filter — the caller wraps that in the pipeline driver.)</item>
///   <item>Provide a <c>ct</c> that is honored throughout the pipeline.</item>
/// </list>
/// <para>
/// Atomicity contract per <c>design/observation-framework.md</c>: the JSON
/// state file is updated atomically once at the end of the phase. If the
/// phase is cancelled mid-extraction, the state file is left untouched and
/// all extraction work for the current dream is lost (acceptable — the next
/// dream picks up from a superset window).
/// </para>
/// </remarks>
public interface IObservationExtractionPhase
{
    /// <summary>
    /// Runs phase 1 for one target. Returns a summary of what was done so
    /// the caller can log/meter outcomes per target.
    /// </summary>
    Task<ExtractionPhaseResult> ExecuteAsync(
        ObservationTarget target,
        IReadOnlyList<TranscriptTurn> transcripts,
        CancellationToken cancellationToken);
}

/// <summary>
/// Summary of a phase 1 execution for one target.
/// </summary>
/// <param name="ConversationsProcessed">Number of distinct conversations
/// whose extraction call completed (success or empty result). Failed
/// conversations are counted under <see cref="ConversationsFailed"/>.</param>
/// <param name="ConversationsFailed">Number of conversations whose
/// extraction call failed and were skipped per the design's "skip and
/// continue" rule.</param>
/// <param name="ProposalsReceived">Total observations proposed by the LLM
/// across all conversations, before quote-grounding.</param>
/// <param name="ProposalsGrounded">Subset of <see cref="ProposalsReceived"/>
/// that survived quote-grounding validation.</param>
/// <param name="MatchedExistingCandidates">Grounded proposals that matched
/// an existing candidate cluster.</param>
/// <param name="NewCandidatesCreated">Grounded proposals that did not match
/// any existing cluster and were promoted to a fresh candidate.</param>
/// <param name="StateWritten">Whether the JSON state file was rewritten.
/// False indicates a no-op (e.g. zero grounded proposals across the batch).</param>
public sealed record ExtractionPhaseResult(
    int ConversationsProcessed,
    int ConversationsFailed,
    int ProposalsReceived,
    int ProposalsGrounded,
    int MatchedExistingCandidates,
    int NewCandidatesCreated,
    bool StateWritten);
