namespace RockBot.Host;

/// <summary>
/// Backend used by <see cref="ILongTermMemory.SearchAsync"/> when scoring candidate entries
/// against a query.
/// </summary>
public enum MemorySearchMode
{
    /// <summary>
    /// Default. BM25 keyword ranking, optionally combined with vector similarity when
    /// embeddings are configured. Recall-oriented and tolerant of paraphrase.
    /// </summary>
    Hybrid = 0,

    /// <summary>
    /// .NET regex pattern matched against the literal stored content (memory path name +
    /// content + tags + category words). Exact and deterministic — preferred when the
    /// caller already knows the literal token (file path, id, version, exact phrase).
    /// </summary>
    Regex = 1,
}
