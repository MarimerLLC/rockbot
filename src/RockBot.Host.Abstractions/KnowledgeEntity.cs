namespace RockBot.Host;

/// <summary>
/// A named entity in the knowledge graph (person, project, topic, etc.).
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Name">Primary display name.</param>
/// <param name="EntityType">The kind of entity.</param>
/// <param name="Aliases">Alternative names or spellings that should resolve to this entity.</param>
/// <param name="Metadata">Arbitrary key-value metadata.</param>
/// <param name="CreatedAt">When the entity was first created.</param>
/// <param name="LastReferencedAt">
/// When this entity was last matched during context expansion (graph retrieval).
/// <c>null</c> if the entity has never been referenced in a user query.
/// Used by the graph consolidation dream pass to identify stale entities.
/// </param>
public sealed record KnowledgeEntity(
    string Id,
    string Name,
    KnowledgeEntityType EntityType,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, string>? Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastReferencedAt = null);
