namespace RockBot.Observation;

/// <summary>
/// Filters the conversation transcript that an observation target sees during
/// phase 1 extraction. Theory-of-self looks at all turns; theory-of-user looks
/// only at user-authored turns plus the agent responses to them. Future targets
/// might want yet other filters, so the filter is pluggable per-target.
/// </summary>
/// <remarks>
/// Filters operate on opaque <see cref="TranscriptTurn"/> records so the framework
/// stays decoupled from specific conversation-log shapes. Adapters that wrap
/// existing conversation logs implement this interface.
/// </remarks>
public interface ITranscriptFilter
{
    /// <summary>
    /// Returns the subset of <paramref name="turns"/> that this target should
    /// extract observations from. The relative order of returned turns must
    /// match the input order so the resulting context is intelligible to the
    /// extractor.
    /// </summary>
    IEnumerable<TranscriptTurn> Filter(IReadOnlyList<TranscriptTurn> turns);
}
