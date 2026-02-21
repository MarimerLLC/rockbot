namespace RockBot.Host;

/// <summary>
/// Session-scoped, TTL-based scratch space for caching tool call results within a conversation.
/// Entries expire automatically based on their TTL and are persisted across pod restarts —
/// live entries are restored on startup so longer-running work can continue uninterrupted.
/// </summary>
public interface IWorkingMemory
{
    /// <summary>Sets or overwrites an entry for <paramref name="key"/> in the given session.</summary>
    Task SetAsync(string sessionId, string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null);

    /// <summary>Returns the cached value, or <c>null</c> if not found or expired.</summary>
    Task<string?> GetAsync(string sessionId, string key);

    /// <summary>Lists all live entries for the session (expired entries are pruned).</summary>
    Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string sessionId);

    /// <summary>Removes a single entry.</summary>
    Task DeleteAsync(string sessionId, string key);

    /// <summary>Removes all entries for the session.</summary>
    Task ClearAsync(string sessionId);

    /// <summary>
    /// Searches live entries for the session using BM25 ranking, with optional
    /// category and tag filters applied before ranking.
    /// Mirrors <see cref="ILongTermMemory.SearchAsync"/> semantics.
    /// </summary>
    Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(string sessionId, MemorySearchCriteria criteria);
}
