namespace RockBot.Host;

/// <summary>What a save actually did to the corpus.</summary>
public enum MemorySaveAction
{
    /// <summary>A new entry was written.</summary>
    Created,

    /// <summary>An existing entry already carried the fact; its reinforcement was raised.</summary>
    Reinforced,

    /// <summary>An existing entry carried most of the fact; the new specifics were appended.</summary>
    Extended,
}

/// <summary>Outcome of a deduplicated save.</summary>
/// <param name="Action">What happened.</param>
/// <param name="Id">Id of the entry that now holds the fact — the new one, or the one reinforced.</param>
/// <param name="Similarity">Score against the matched entry, when one was matched.</param>
/// <param name="Measure">Which measure produced <paramref name="Similarity"/>.</param>
public sealed record MemorySaveOutcome(
    MemorySaveAction Action,
    string Id,
    double? Similarity = null,
    MemorySimilarityMeasure? Measure = null);

/// <summary>
/// Save path that reinforces an existing entry rather than adding a near-copy of it.
/// </summary>
/// <remarks>
/// <para>
/// The corpus this was built for grew from 290 to 828 live entries in 24 days while
/// consolidation archived 885 — the same duplicate clusters proposed, merged, re-created and
/// proposed again. Deduplicating at save time is the root fix: consolidation can only ever
/// clean up duplicates that already exist, and it costs an LLM call per cycle to do it.
/// </para>
/// <para>
/// Deliberately not part of <see cref="ILongTermMemory"/>. Some writes must land verbatim —
/// merges carry their own ids and provenance, identity reflection rewrites its own entries,
/// contradiction resolution supersedes rather than merges — so this is a decision each caller
/// opts into, not a behaviour every <c>SaveAsync</c> inherits.
/// </para>
/// </remarks>
public interface IMemoryDeduplicator
{
    /// <summary>
    /// Saves <paramref name="candidate"/>, or folds it into the live entry that already carries
    /// the same fact.
    /// </summary>
    /// <remarks>
    /// Folding takes one of two forms. When the existing entry already contains every specific
    /// the candidate carries, the candidate adds nothing but evidence, so only the reinforcement
    /// counters move. When it carries new specifics, they are appended rather than discarded —
    /// the alternative is a silent loss, which is the failure this whole area exists to prevent.
    /// </remarks>
    Task<MemorySaveOutcome> SaveOrReinforceAsync(
        MemoryEntry candidate,
        CancellationToken cancellationToken = default);
}
