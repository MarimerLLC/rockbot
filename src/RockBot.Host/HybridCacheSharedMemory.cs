using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// <see cref="ISharedMemory"/> backed by <see cref="IMemoryCache"/> for TTL-based eviction,
/// with a side index (<see cref="ConcurrentDictionary{TKey,TValue}"/>) for key enumeration.
/// Unlike <see cref="HybridCacheWorkingMemory"/>, keys are global — not session-scoped.
/// </summary>
internal sealed class HybridCacheSharedMemory : ISharedMemory
{
    private readonly IMemoryCache _cache;
    private readonly SharedMemoryOptions _options;
    private readonly ILogger<HybridCacheSharedMemory> _logger;

    // key -> EntryMeta (no session nesting)
    private readonly ConcurrentDictionary<string, EntryMeta> _index = new();

    private sealed record EntryMeta(
        DateTimeOffset StoredAt,
        DateTimeOffset ExpiresAt,
        string? Category,
        IReadOnlyList<string> Tags);

    public HybridCacheSharedMemory(
        IMemoryCache cache,
        IOptions<SharedMemoryOptions> options,
        ILogger<HybridCacheSharedMemory> logger)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    private static string CacheKey(string key) => $"sm:{key}";

    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        var effectiveTtl = ttl ?? _options.DefaultTtl;
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + effectiveTtl;

        // Enforce MaxEntries for new keys only
        if (!_index.ContainsKey(key) && _index.Count >= _options.MaxEntries)
        {
            _logger.LogWarning(
                "Shared memory limit reached ({Max} entries); ignoring key '{Key}'",
                _options.MaxEntries, key);
            return Task.CompletedTask;
        }

        _index[key] = new EntryMeta(now, expiresAt, category, tags ?? []);
        _cache.Set(CacheKey(key), value, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt
        });

        _logger.LogDebug("Shared memory set: key={Key} ttl={Ttl}", key, effectiveTtl);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string key)
    {
        if (!_index.TryGetValue(key, out var meta) || meta.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _index.TryRemove(key, out _);
            return Task.FromResult<string?>(null);
        }

        _cache.TryGetValue<string>(CacheKey(key), out var value);
        return Task.FromResult(value);
    }

    public Task<IReadOnlyList<SharedMemoryEntry>> ListAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<SharedMemoryEntry>();

        foreach (var kvp in _index.ToArray()) // snapshot for safe iteration
        {
            if (kvp.Value.ExpiresAt <= now)
            {
                _index.TryRemove(kvp.Key, out _);
                continue;
            }

            if (_cache.TryGetValue<string>(CacheKey(kvp.Key), out var value))
            {
                var meta = kvp.Value;
                entries.Add(new SharedMemoryEntry(kvp.Key, value!, meta.StoredAt, meta.ExpiresAt, meta.Category, meta.Tags));
            }
            else
            {
                // Evicted under memory pressure — prune from index
                _index.TryRemove(kvp.Key, out _);
            }
        }

        return Task.FromResult<IReadOnlyList<SharedMemoryEntry>>(entries);
    }

    public Task DeleteAsync(string key)
    {
        _index.TryRemove(key, out _);
        _cache.Remove(CacheKey(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        foreach (var key in _index.Keys)
            _cache.Remove(CacheKey(key));
        _index.Clear();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SharedMemoryEntry>> SearchAsync(MemorySearchCriteria criteria)
    {
        var allEntries = await ListAsync();
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

    private static string GetDocumentText(SharedMemoryEntry entry)
    {
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

    private static bool PassesStructuralFilters(SharedMemoryEntry entry, MemorySearchCriteria criteria)
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
