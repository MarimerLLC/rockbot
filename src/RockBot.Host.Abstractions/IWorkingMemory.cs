namespace RockBot.Host;

/// <summary>
/// Session-scoped, TTL-based scratch space for caching tool call results within a conversation.
/// Entries expire automatically and are never persisted — designed for large or expensive-to-fetch
/// data that the LLM can reference across turns without re-calling the external source.
/// </summary>
public interface IWorkingMemory
{
    /// <summary>Sets or overwrites an entry for <paramref name="key"/> in the given session.</summary>
    Task SetAsync(string sessionId, string key, string value, TimeSpan? ttl = null);

    /// <summary>Returns the cached value, or <c>null</c> if not found or expired.</summary>
    Task<string?> GetAsync(string sessionId, string key);

    /// <summary>Lists all live entries for the session (expired entries are pruned).</summary>
    Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string sessionId);

    /// <summary>Removes a single entry.</summary>
    Task DeleteAsync(string sessionId, string key);

    /// <summary>Removes all entries for the session.</summary>
    Task ClearAsync(string sessionId);
}
