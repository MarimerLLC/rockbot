using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// <see cref="IWorkingMemory"/> backed by <see cref="IMemoryCache"/> for TTL-based eviction,
/// with a side index (<see cref="ConcurrentDictionary{TKey,TValue}"/>) for key enumeration
/// (which <c>IMemoryCache</c> does not support natively).
/// </summary>
internal sealed class HybridCacheWorkingMemory : IWorkingMemory
{
    private readonly IMemoryCache _cache;
    private readonly WorkingMemoryOptions _options;
    private readonly ILogger<HybridCacheWorkingMemory> _logger;

    // sessionId -> { key -> (storedAt, expiresAt) }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (DateTimeOffset StoredAt, DateTimeOffset ExpiresAt)>> _index = new();

    public HybridCacheWorkingMemory(
        IMemoryCache cache,
        IOptions<WorkingMemoryOptions> options,
        ILogger<HybridCacheWorkingMemory> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    private static string CacheKey(string sessionId, string key) => $"wm:{sessionId}:{key}";

    public Task SetAsync(string sessionId, string key, string value, TimeSpan? ttl = null)
    {
        var effectiveTtl = ttl ?? _options.DefaultTtl;
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + effectiveTtl;

        var sessionIndex = _index.GetOrAdd(sessionId, _ => new());

        // Enforce MaxEntriesPerSession for new keys only
        if (!sessionIndex.ContainsKey(key) && sessionIndex.Count >= _options.MaxEntriesPerSession)
        {
            _logger.LogWarning(
                "Working memory limit reached for session {SessionId} ({Max} entries); ignoring key '{Key}'",
                sessionId, _options.MaxEntriesPerSession, key);
            return Task.CompletedTask;
        }

        sessionIndex[key] = (now, expiresAt);
        _cache.Set(CacheKey(sessionId, key), value, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt
        });

        _logger.LogDebug("Working memory set: session={SessionId} key={Key} ttl={Ttl}", sessionId, key, effectiveTtl);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string sessionId, string key)
    {
        if (!_index.TryGetValue(sessionId, out var sessionIndex))
            return Task.FromResult<string?>(null);

        if (!sessionIndex.TryGetValue(key, out var meta) || meta.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            sessionIndex.TryRemove(key, out _);
            return Task.FromResult<string?>(null);
        }

        _cache.TryGetValue<string>(CacheKey(sessionId, key), out var value);
        return Task.FromResult(value);
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string sessionId)
    {
        if (!_index.TryGetValue(sessionId, out var sessionIndex))
            return Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>([]);

        var now = DateTimeOffset.UtcNow;
        var entries = new List<WorkingMemoryEntry>();

        foreach (var kvp in sessionIndex.ToArray()) // snapshot for safe iteration
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                sessionIndex.TryRemove(kvp.Key, out _);
                continue;
            }

            if (_cache.TryGetValue<string>(CacheKey(sessionId, kvp.Key), out var value))
            {
                entries.Add(new WorkingMemoryEntry(kvp.Key, value!, kvp.Value.StoredAt, kvp.Value.ExpiresAt));
            }
            else
            {
                // Evicted under memory pressure — prune from index
                sessionIndex.TryRemove(kvp.Key, out _);
            }
        }

        return Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>(entries);
    }

    public Task DeleteAsync(string sessionId, string key)
    {
        if (_index.TryGetValue(sessionId, out var sessionIndex))
            sessionIndex.TryRemove(key, out _);

        _cache.Remove(CacheKey(sessionId, key));
        return Task.CompletedTask;
    }

    public Task ClearAsync(string sessionId)
    {
        if (_index.TryRemove(sessionId, out var sessionIndex))
        {
            foreach (var key in sessionIndex.Keys)
                _cache.Remove(CacheKey(sessionId, key));
        }

        return Task.CompletedTask;
    }
}
