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
    /// Replaces an exact piece of text inside an existing entry's value, leaving the rest of
    /// it untouched. The entry keeps its category and tags, and its TTL restarts from now
    /// using the same window it was originally stored with.
    /// </summary>
    /// <param name="key">Full-path key of the entry to edit.</param>
    /// <param name="oldText">Exact text to find. Must be non-empty.</param>
    /// <param name="newText">Replacement text. May be empty to delete the match.</param>
    /// <param name="replaceAll">
    /// When <c>true</c>, replaces every occurrence. When <c>false</c>, more than one
    /// occurrence is refused rather than guessed at.
    /// </param>
    /// <remarks>
    /// Refreshing the TTL is the deliberate choice: an entry someone is actively amending is
    /// an entry still in use, and expiring it on the original clock would drop it moments
    /// after a correction. The window is reused rather than reset to the default so a
    /// deliberately long-lived handoff entry does not silently become a five-minute one.
    /// </remarks>
    Task<ContentEditResult> EditAsync(string key, string oldText, string newText, bool replaceAll = false)
        => Task.FromResult(ContentEditResult.NotSupported);

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
