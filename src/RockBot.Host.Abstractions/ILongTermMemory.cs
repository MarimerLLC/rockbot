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
    /// <remarks>
    /// This is a hard delete with no recovery path. Automated housekeeping should call
    /// <see cref="ArchiveAsync"/> instead and leave hard deletion to the retention purge
    /// and to explicit user-driven removal.
    /// </remarks>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives a memory entry by ID — hides it from search while keeping it on disk and
    /// retrievable by ID. No-op if not found or already archived.
    /// </summary>
    /// <param name="id">Entry to archive.</param>
    /// <param name="reason">Human-readable justification recorded on the entry.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The default implementation falls back to <see cref="DeleteAsync"/> for stores with no
    /// archive tier. Any store that can retain data should override it: the whole point of
    /// this method is that a wrong automated removal stays recoverable.
    /// </remarks>
    Task ArchiveAsync(string id, string reason, CancellationToken cancellationToken = default)
        => DeleteAsync(id, cancellationToken);

    /// <summary>
    /// Returns all distinct tags across all memory entries.
    /// </summary>
    Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all distinct categories across all memory entries.
    /// </summary>
    Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default);
}
