namespace RockBot.Host;

/// <summary>
/// Options for the file-backed knowledge graph store.
/// </summary>
public sealed class KnowledgeGraphOptions
{
    /// <summary>
    /// Base directory for knowledge graph JSON files, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string BasePath { get; set; } = "knowledge-graph";

    /// <summary>
    /// Maximum number of hops for graph traversal during context expansion.
    /// </summary>
    public int MaxHops { get; set; } = 2;

    /// <summary>
    /// Maximum number of triples to inject into the LLM context after graph expansion.
    /// </summary>
    public int MaxExpandedTriples { get; set; } = 15;

    /// <summary>
    /// Maximum number of top-ranked recalled memories that may contribute additional BFS
    /// seeds to knowledge graph expansion. Retrieval rank is used as the admission proxy
    /// (scores are not surfaced by <see cref="ILongTermMemory"/>). Set to 0 to disable
    /// memory-derived seeding entirely. Default is 2 — conservative enough that BM25-only
    /// deployments will not amplify keyword-collision noise.
    /// </summary>
    public int MaxMemorySeedSources { get; set; } = 2;

    /// <summary>
    /// Asymmetric hop budget applied to memory-derived seeds only. Memory seeds are one
    /// inference step removed from the user's intent, so their neighborhood is intentionally
    /// shallower than <see cref="MaxHops"/>. Default is 1.
    /// </summary>
    public int MemorySeedMaxHops { get; set; } = 1;
}
