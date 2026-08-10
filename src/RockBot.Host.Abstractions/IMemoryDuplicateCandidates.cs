namespace RockBot.Host;

/// <summary>
/// Optional capability for stores that can identify which entries plausibly duplicate each
/// other, so callers can act on a small candidate set instead of the whole corpus.
/// </summary>
/// <remarks>
/// <para>
/// This exists to bound what dream consolidation is allowed to touch. Handing the LLM every
/// entry each cycle means every entry is re-tried for deletion on every cycle, and survival
/// compounds: at twice a day, a one-in-a-thousand misjudgement per entry per cycle loses
/// roughly half the corpus in a year. Gating turns an unbounded repeated gamble into a
/// decision made once per entry.
/// </para>
/// <para>
/// Clustering lives behind the store because only the store knows whether embeddings are
/// available. Implementations should degrade to a lexical measure rather than returning
/// nothing when they are not — a BM25-only deployment still needs deduplication.
/// </para>
/// </remarks>
public interface IMemoryDuplicateCandidates
{
    /// <summary>
    /// Groups entries that plausibly describe the same fact. Only entries with at least one
    /// sibling above <paramref name="similarityThreshold"/> appear; singletons are omitted.
    /// </summary>
    /// <param name="similarityThreshold">
    /// Minimum similarity (0..1) for two entries to land in the same cluster. Interpretation
    /// is implementation-defined — cosine over embeddings, or a lexical measure as a fallback.
    /// </param>
    /// <param name="maxClusterSize">
    /// Ceiling on entries per cluster. Oversized clusters are split rather than dropped, so a
    /// single sprawling topic cannot be collapsed into one entry in one pass.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Clusters of entry IDs, each with at least two members.</returns>
    Task<IReadOnlyList<IReadOnlyList<string>>> FindNearDuplicateClustersAsync(
        double similarityThreshold,
        int maxClusterSize,
        CancellationToken cancellationToken = default);
}
