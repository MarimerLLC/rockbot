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
    /// Replaces an exact piece of text inside an existing entry's content, leaving every
    /// other field — including <see cref="MemoryEntry.CreatedAt"/>,
    /// <see cref="MemoryEntry.LastSeenAt"/>, and
    /// <see cref="MemoryEntry.ReinforcementCount"/> — untouched.
    /// </summary>
    /// <param name="id">Entry to edit.</param>
    /// <param name="oldText">Exact text to find. Must be non-empty.</param>
    /// <param name="newText">Replacement text. May be empty to delete the match.</param>
    /// <param name="replaceAll">
    /// When <c>true</c>, replaces every occurrence. When <c>false</c>, more than one
    /// occurrence is refused rather than guessed at.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The correction path that <see cref="DeleteAsync"/> plus <see cref="SaveAsync"/> cannot
    /// be: that pair mints a new id and resets the entry's provenance, so fixing one word in a
    /// fact reinforced two hundred times leaves a fact seen once. Implementations must apply
    /// the whole read-modify-write cycle under whatever lock guards their other writes, so a
    /// concurrent save cannot be silently discarded.
    /// </remarks>
    Task<ContentEditResult> EditAsync(
        string id,
        string oldText,
        string newText,
        bool replaceAll = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ContentEditResult.NotSupported);

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
    /// This is a hard delete with no recovery path. Only the retention purge should call it.
    /// Every other removal — consolidation, identity reflection, the agent's own delete tool,
    /// capability-claim eviction, aged observation theories — goes through
    /// <see cref="ArchiveAsync"/>, so a wrong automated judgement costs recall until someone
    /// notices rather than costing the fact.
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
