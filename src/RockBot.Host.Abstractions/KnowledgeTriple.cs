namespace RockBot.Host;

/// <summary>
/// A subject-predicate-object triple representing a relationship in the knowledge graph.
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="Subject">Entity ID or name (the source of the relationship).</param>
/// <param name="Predicate">Relationship type, e.g. "created", "works_on", "knows", "uses".</param>
/// <param name="Object">Entity ID, name, or literal value (the target of the relationship).</param>
/// <param name="Confidence">Confidence score from 0.0 to 1.0.</param>
/// <param name="SourceEpisodeId">Optional ID of the episodic memory entry that produced this triple.</param>
/// <param name="CreatedAt">When the triple was created.</param>
public sealed record KnowledgeTriple(
    string Id,
    string Subject,
    string Predicate,
    string Object,
    float Confidence,
    string? SourceEpisodeId,
    DateTimeOffset CreatedAt);
