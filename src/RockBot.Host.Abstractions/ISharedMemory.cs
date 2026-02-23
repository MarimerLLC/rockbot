namespace RockBot.Host;

/// <summary>
/// Cross-session, TTL-based scratch space accessible to all execution contexts
/// (user sessions, patrol tasks, subagents). Unlike <see cref="IWorkingMemory"/>,
/// keys are global — not scoped to a session. No LLM processing is applied to entries.
/// </summary>
public interface ISharedMemory
{
    /// <summary>Sets or overwrites an entry for <paramref name="key"/>.</summary>
    Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null);

    /// <summary>Returns the cached value, or <c>null</c> if not found or expired.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>Lists all live entries (expired entries are pruned).</summary>
    Task<IReadOnlyList<SharedMemoryEntry>> ListAsync();

    /// <summary>Removes a single entry.</summary>
    Task DeleteAsync(string key);

    /// <summary>Removes all entries.</summary>
    Task ClearAsync();

    /// <summary>
    /// Searches live entries using BM25 ranking, with optional
    /// category and tag filters applied before ranking.
    /// </summary>
    Task<IReadOnlyList<SharedMemoryEntry>> SearchAsync(MemorySearchCriteria criteria);
}
