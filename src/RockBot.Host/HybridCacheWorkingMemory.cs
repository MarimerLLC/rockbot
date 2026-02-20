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

    // sessionId -> { key -> EntryMeta }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, EntryMeta>> _index = new();

    private sealed record EntryMeta(
        DateTimeOffset StoredAt,
        DateTimeOffset ExpiresAt,
        string? Category,
        IReadOnlyList<string> Tags);

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

    public Task SetAsync(string sessionId, string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
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

        sessionIndex[key] = new EntryMeta(now, expiresAt, category, tags ?? []);
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
                var meta = kvp.Value;
                entries.Add(new WorkingMemoryEntry(kvp.Key, value!, meta.StoredAt, meta.ExpiresAt, meta.Category, meta.Tags));
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

    public async Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(string sessionId, MemorySearchCriteria criteria)
    {
        var allEntries = await ListAsync(sessionId);
        if (allEntries.Count == 0)
            return allEntries;

        // Apply structural filters (category prefix + tag intersection)
        var candidates = allEntries.Where(e => PassesStructuralFilters(e, criteria)).ToList();

        // No query: return most-recently stored entries up to MaxResults
        if (criteria.Query is null)
            return candidates.OrderByDescending(e => e.StoredAt).Take(criteria.MaxResults).ToList();

        // With query: BM25 ranking
        return Bm25Ranker.Rank(candidates, GetDocumentText, criteria.Query)
            .Take(criteria.MaxResults)
            .ToList();
    }

    // ── BM25 document text ────────────────────────────────────────────────────

    private static string GetDocumentText(WorkingMemoryEntry entry)
    {
        // Include the key (with punctuation normalised) so searches like "calendar" find
        // an entry stored under key "calendar_2026-03" even if the value doesn't mention it.
        var parts = new List<string>
        {
            entry.Key.Replace('_', ' ').Replace('-', ' '),
            entry.Value
        };
        if (entry.Tags is { Count: > 0 })
            parts.Add(string.Join(" ", entry.Tags));
        if (entry.Category is not null)
            parts.Add(entry.Category.Replace('/', ' ').Replace('-', ' '));
        return string.Join(" ", parts);
    }

    // ── Structural filters ────────────────────────────────────────────────────

    private static bool PassesStructuralFilters(WorkingMemoryEntry entry, MemorySearchCriteria criteria)
    {
        if (criteria.Category is not null)
        {
            if (entry.Category is null) return false;

            // Prefix match: "pricing" matches "pricing" and "pricing/strategies"
            if (!entry.Category.Equals(criteria.Category, StringComparison.OrdinalIgnoreCase) &&
                !entry.Category.StartsWith(criteria.Category + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (criteria.Tags is { Count: > 0 })
        {
            var entryTags = entry.Tags ?? [];
            if (!criteria.Tags.All(tag =>
                    entryTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))))
                return false;
        }

        return true;
    }
}
