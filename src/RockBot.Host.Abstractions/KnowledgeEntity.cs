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
public sealed record KnowledgeEntity(
    string Id,
    string Name,
    KnowledgeEntityType EntityType,
    IReadOnlyList<string> Aliases,
    IReadOnlyDictionary<string, string>? Metadata,
    DateTimeOffset CreatedAt);
