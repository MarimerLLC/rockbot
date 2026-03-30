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
}
