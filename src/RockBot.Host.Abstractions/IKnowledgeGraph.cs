namespace RockBot.Host;

/// <summary>
/// Knowledge graph store for entity-relationship reasoning.
/// Stores entities and subject-predicate-object triples, with traversal support.
/// </summary>
public interface IKnowledgeGraph
{
    /// <summary>
    /// Adds or updates an entity. If an entity with the same ID exists, it is overwritten.
    /// </summary>
    Task SaveEntityAsync(KnowledgeEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an entity by ID, or null if not found.
    /// </summary>
    Task<KnowledgeEntity?> GetEntityAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds entities whose name or aliases match the query (case-insensitive substring).
    /// </summary>
    Task<IReadOnlyList<KnowledgeEntity>> FindEntitiesByNameAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity by ID and all triples that reference it as subject or object. No-op if not found.
    /// </summary>
    Task DeleteEntityAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all entities in the graph.
    /// </summary>
    Task<IReadOnlyList<KnowledgeEntity>> ListEntitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a triple. If a triple with the same ID exists, it is overwritten.
    /// </summary>
    Task SaveTripleAsync(KnowledgeTriple triple, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all triples where the given entity ID or name appears as the subject.
    /// </summary>
    Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForSubjectAsync(string subjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all triples where the given entity ID or name appears as the object.
    /// </summary>
    Task<IReadOnlyList<KnowledgeTriple>> GetTriplesForObjectAsync(string objectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a breadth-first traversal from the given seed entity IDs, up to <paramref name="maxHops"/> hops,
    /// returning all triples discovered along the way.
    /// </summary>
    Task<IReadOnlyList<KnowledgeTriple>> TraverseAsync(
        IReadOnlyList<string> seedEntityIds,
        int maxHops = 2,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a triple by ID. No-op if not found.
    /// </summary>
    Task DeleteTripleAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates <see cref="KnowledgeEntity.LastReferencedAt"/> to the current time for the given entity IDs.
    /// No-op for IDs that do not exist. Used by the context builder to track which entities
    /// are actively useful so the consolidation pass can identify stale ones.
    /// </summary>
    Task TouchEntitiesAsync(IReadOnlyList<string> entityIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all triples in the graph.
    /// </summary>
    Task<IReadOnlyList<KnowledgeTriple>> ListTriplesAsync(CancellationToken cancellationToken = default);
}
