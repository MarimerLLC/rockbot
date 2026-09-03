namespace RockBot.Host;

/// <summary>
/// Options for long-term memory storage.
/// When <see cref="BasePath"/> is relative, it is resolved under <see cref="AgentProfileOptions.BasePath"/>.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>
    /// Base directory for memory files. Defaults to <c>"memory"</c>.
    /// When relative, resolved under the agent profile base path.
    /// When absolute, used directly.
    /// </summary>
    public string BasePath { get; set; } = "memory";

    /// <summary>
    /// Whether a save looks for an existing near-duplicate and reinforces it instead of writing
    /// a second copy. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// Turning this off restores the pre-deduplication behaviour: every save creates an entry
    /// and dream consolidation is left to merge the duplicates back together afterwards. Only
    /// worth doing to isolate a suspected mis-match, since the cost is a corpus that grows
    /// without converging.
    /// </remarks>
    public bool DedupeEnabled { get; set; } = true;

    /// <summary>
    /// Cosine similarity at or above which an incoming entry is folded into the existing one it
    /// matched. Defaults to 0.88.
    /// </summary>
    /// <remarks>
    /// Keep this aligned with <c>DreamOptions.ConsolidationSimilarityThreshold</c>. The two
    /// answer the same question at different times: anything caught here would have been
    /// clustered as a near-duplicate by consolidation anyway, so a save-time threshold looser
    /// than consolidation's folds entries consolidation would not have touched, and a tighter
    /// one leaves work for consolidation that the save could have avoided creating.
    /// </remarks>
    public double DedupeSimilarityThreshold { get; set; } = 0.88;

    /// <summary>
    /// Jaccard overlap at or above which an incoming entry is folded, on stores with no
    /// embeddings. Defaults to 0.6.
    /// </summary>
    /// <remarks>
    /// A different scale from the cosine threshold and necessarily a lower number: token overlap
    /// of 3 shared words out of 4, or 4 out of 6, is a restatement; 3 out of 7 is not. The gap
    /// between the two figures is not a discrepancy to tidy up. A rephrasing that shares little
    /// vocabulary is deliberately left to consolidation, which has an LLM to judge it with.
    /// </remarks>
    public double DedupeLexicalSimilarityThreshold { get; set; } = 0.6;

    /// <summary>
    /// Longest combined content, in characters, that may be produced by appending new specifics
    /// to a matched entry. Past this the candidate is saved as its own entry. Defaults to 2000.
    /// </summary>
    /// <remarks>
    /// Without a ceiling, a topic the agent revisits constantly accretes into one entry that
    /// keeps matching itself — a single "Blazor Online Class" record a hundred paragraphs long,
    /// which recall then surfaces in full for any question that brushes the subject.
    /// </remarks>
    public int DedupeMaxExtendedContentLength { get; set; } = 2000;
}
