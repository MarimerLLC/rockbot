namespace RockBot.Host;

/// <summary>How a similarity score was computed, which is what its scale means.</summary>
public enum MemorySimilarityMeasure
{
    /// <summary>Cosine similarity over embedding vectors.</summary>
    Embedding,

    /// <summary>Jaccard overlap over content tokens — the fallback where embeddings are absent.</summary>
    Lexical,
}

/// <summary>The live entry most similar to a candidate, and how similar it is.</summary>
/// <param name="Entry">The existing entry.</param>
/// <param name="Score">Similarity in 0..1, on the scale named by <paramref name="Measure"/>.</param>
/// <param name="Measure">Which measure produced <paramref name="Score"/>.</param>
public sealed record MemorySimilarityMatch(
    MemoryEntry Entry,
    double Score,
    MemorySimilarityMeasure Measure);

/// <summary>
/// Optional capability for stores that can name the single existing entry a candidate most
/// resembles, so a save can reinforce what is already there instead of adding a near-copy.
/// </summary>
/// <remarks>
/// <para>
/// Nothing on the save path ever looked for a near-duplicate before writing. Reinforcement was
/// raised only by episode extraction and by merges summing their sources, so an entry the live
/// corpus had "reinforced 243 times" had in fact been saved fresh 243 times and merged back
/// together afterwards. Consolidation was left doing the deduplication that the save should
/// have avoided needing, twice a day, forever.
/// </para>
/// <para>
/// Separate from <see cref="ILongTermMemory"/> for the same reason as
/// <see cref="IMemoryDuplicateCandidates"/>: only the store knows whether it has embeddings, and
/// a store that has neither them nor a lexical fallback should be able to decline rather than
/// answer badly. Callers probe for it and save unconditionally when it is absent.
/// </para>
/// </remarks>
public interface IMemorySimilarityLookup
{
    /// <summary>
    /// Returns the live entry most similar to <paramref name="candidate"/>, or <c>null</c> when
    /// there is nothing comparable to it.
    /// </summary>
    /// <param name="candidate">
    /// The entry about to be saved. Passed whole rather than as text because implementations
    /// that use embeddings must build the query from the same surface their cached vectors were
    /// built from — content plus tags plus category words, not content alone.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Implementations should compare only entries that could actually be reinforced: live
    /// (neither archived nor superseded), not the candidate itself, and in the same broad
    /// subject area, so a lexical fallback cannot pair a user preference with an infrastructure
    /// note that happens to share vocabulary.
    /// </remarks>
    Task<MemorySimilarityMatch?> FindMostSimilarAsync(
        MemoryEntry candidate,
        CancellationToken cancellationToken = default);
}
