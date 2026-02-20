namespace RockBot.Host;

/// <summary>
/// Persistent long-term memory store for agent knowledge.
/// Designed for future swap to vector/embedding store.
/// </summary>
public interface ILongTermMemory
{
    /// <summary>
    /// Saves a memory entry. If an entry with the same ID exists, it is overwritten.
    /// </summary>
    Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches memory entries matching the given criteria.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single memory entry by ID, or null if not found.
    /// </summary>
    Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a memory entry by ID. No-op if not found.
    /// </summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all distinct tags across all memory entries.
    /// </summary>
    Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all distinct categories across all memory entries.
    /// </summary>
    Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default);
}
