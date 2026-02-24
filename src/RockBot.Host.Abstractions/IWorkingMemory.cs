namespace RockBot.Host;

/// <summary>
/// Global, TTL-based working memory — a path-namespaced scratch space accessible to all
/// execution contexts (user sessions, patrol tasks, subagents). Keys are full path strings
/// such as <c>session/abc123/emails</c>, <c>patrol/heartbeat/alert</c>, or
/// <c>subagent/task1/result</c>. The path prefix provides namespace isolation while
/// allowing any context to read across namespaces.
/// </summary>
public interface IWorkingMemory
{
    /// <summary>Sets or overwrites an entry for <paramref name="key"/>.</summary>
    Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null);

    /// <summary>Returns the cached value, or <c>null</c> if not found or expired.</summary>
    Task<string?> GetAsync(string key);

    /// <summary>
    /// Lists all live entries whose key starts with <paramref name="prefix"/> (expired entries
    /// are pruned). Pass <c>null</c> or empty string to list everything.
    /// </summary>
    Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null);

    /// <summary>Removes a single entry.</summary>
    Task DeleteAsync(string key);

    /// <summary>
    /// Removes all entries whose key starts with <paramref name="prefix"/>.
    /// Clears everything when <paramref name="prefix"/> is <c>null</c>.
    /// </summary>
    Task ClearAsync(string? prefix = null);

    /// <summary>
    /// Searches live entries using BM25 ranking, with optional category and tag filters
    /// applied before ranking. Pass <paramref name="prefix"/> to restrict the search to
    /// a namespace (e.g. <c>"patrol/heartbeat"</c>).
    /// </summary>
    Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null);
}
