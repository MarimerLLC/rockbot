using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// <see cref="IWorkingMemory"/> backed by <see cref="IMemoryCache"/> for TTL-based eviction,
/// with a flat <see cref="ConcurrentDictionary{TKey,TValue}"/> side-index for key enumeration.
/// Keys are full path strings (e.g. <c>session/abc123/emails</c>, <c>patrol/heartbeat/alert</c>).
/// </summary>
internal sealed class HybridCacheWorkingMemory : IWorkingMemory
{
    private readonly IMemoryCache _cache;
    private readonly WorkingMemoryOptions _options;
    private readonly ILogger<HybridCacheWorkingMemory> _logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly int _maxEmbeddingInputChars;
    private readonly float _minSimilarity;

    // fullKey -> EntryMeta
    private readonly ConcurrentDictionary<string, EntryMeta> _index = new(StringComparer.OrdinalIgnoreCase);

    // fullKey -> float[] (in-memory only, evicted with the entry)
    private readonly ConcurrentDictionary<string, float[]> _embeddings = new(StringComparer.OrdinalIgnoreCase);

    private sealed record EntryMeta(
        DateTimeOffset StoredAt,
        DateTimeOffset ExpiresAt,
        string? Category,
        IReadOnlyList<string> Tags);

    public HybridCacheWorkingMemory(
        IMemoryCache cache,
        IOptions<WorkingMemoryOptions> options,
        IOptions<EmbeddingOptions> embeddingOptions,
        ILogger<HybridCacheWorkingMemory> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
        _embeddingGenerator = embeddingGenerator;
        _maxEmbeddingInputChars = embeddingOptions.Value.MaxInputChars;
        _minSimilarity = embeddingOptions.Value.MinSimilarityThreshold;
    }

    private static string CacheKey(string key) => $"wm:{key}";

    /// <summary>
    /// Returns the namespace for a key — the first two path segments
    /// (e.g. <c>session/abc123</c> from <c>session/abc123/emails</c>).
    /// Used for per-namespace entry limits.
    /// </summary>
    private static string GetNamespace(string key)
    {
        var slash1 = key.IndexOf('/');
        if (slash1 < 0) return key;
        var slash2 = key.IndexOf('/', slash1 + 1);
        return slash2 < 0 ? key : key[..slash2];
    }

    public Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        var effectiveTtl = ttl ?? _options.DefaultTtl;
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + effectiveTtl;

        var ns = GetNamespace(key);
        var nsCount = _index.Count(kvp =>
            GetNamespace(kvp.Key).Equals(ns, StringComparison.OrdinalIgnoreCase) &&
            kvp.Value.ExpiresAt > now);

        if (!_index.ContainsKey(key) && nsCount >= _options.MaxEntriesPerNamespace)
        {
            _logger.LogWarning(
                "Working memory limit reached for namespace '{Namespace}' ({Max} entries); ignoring key '{Key}'",
                ns, _options.MaxEntriesPerNamespace, key);
            return Task.CompletedTask;
        }

        _index[key] = new EntryMeta(now, expiresAt, category, tags ?? []);
        _cache.Set(CacheKey(key), value, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt
        });

        _logger.LogDebug("Working memory set: key={Key} ttl={Ttl}", key, effectiveTtl);

        // Generate embedding in the background (best-effort, non-blocking)
        if (_embeddingGenerator is not null)
        {
            _ = GenerateEmbeddingAsync(key, value, category, tags);
        }

        return Task.CompletedTask;
    }

    private async Task GenerateEmbeddingAsync(string key, string value, string? category, IReadOnlyList<string>? tags)
    {
        try
        {
            var docText = BuildDocumentText(key, value, category, tags);
            if (docText.Length > _maxEmbeddingInputChars)
                docText = docText[.._maxEmbeddingInputChars];
            var result = await _embeddingGenerator!.GenerateAsync(docText);

            // Only store if the entry still exists — prevents orphaned embeddings
            // when the entry was deleted while generation was in flight.
            if (_index.ContainsKey(key))
                _embeddings[key] = result.Vector.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate working memory embedding for key {Key}", key);
        }
    }

    private static string BuildDocumentText(string key, string value, string? category, IReadOnlyList<string>? tags)
    {
        var parts = new List<string>
        {
            key.Replace('_', ' ').Replace('-', ' ').Replace('/', ' '),
            value
        };
        if (tags is { Count: > 0 })
            parts.Add(string.Join(" ", tags));
        if (category is not null)
            parts.Add(category.Replace('/', ' ').Replace('-', ' '));
        return string.Join(" ", parts);
    }

    public Task<string?> GetAsync(string key)
    {
        if (!_index.TryGetValue(key, out var meta) || meta.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _index.TryRemove(key, out _);
            _embeddings.TryRemove(key, out _);
            return Task.FromResult<string?>(null);
        }

        _cache.TryGetValue<string>(CacheKey(key), out var value);
        return Task.FromResult(value);
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new List<WorkingMemoryEntry>();

        foreach (var kvp in _index.ToArray()) // snapshot for safe iteration
        {
            if (!MatchesPrefix(kvp.Key, prefix))
                continue;

            if (kvp.Value.ExpiresAt <= now)
            {
                _index.TryRemove(kvp.Key, out _);
                _embeddings.TryRemove(kvp.Key, out _);
                continue;
            }

            if (_cache.TryGetValue<string>(CacheKey(kvp.Key), out var value))
            {
                var meta = kvp.Value;
                entries.Add(new WorkingMemoryEntry(kvp.Key, value!, meta.StoredAt, meta.ExpiresAt, meta.Category, meta.Tags));
            }
            else
            {
                // Evicted under memory pressure — prune from index
                _index.TryRemove(kvp.Key, out _);
                _embeddings.TryRemove(kvp.Key, out _);
            }
        }

        return Task.FromResult<IReadOnlyList<WorkingMemoryEntry>>(entries);
    }

    public Task DeleteAsync(string key)
    {
        _index.TryRemove(key, out _);
        _embeddings.TryRemove(key, out _);
        _cache.Remove(CacheKey(key));
        return Task.CompletedTask;
    }

    public Task ClearAsync(string? prefix = null)
    {
        foreach (var kvp in _index.ToArray())
        {
            if (!MatchesPrefix(kvp.Key, prefix))
                continue;

            _index.TryRemove(kvp.Key, out _);
            _embeddings.TryRemove(kvp.Key, out _);
            _cache.Remove(CacheKey(kvp.Key));
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null)
    {
        var allEntries = await ListAsync(prefix);
        if (allEntries.Count == 0)
            return allEntries;

        // Apply structural filters (category prefix + tag intersection)
        var candidates = allEntries.Where(e => PassesStructuralFilters(e, criteria)).ToList();

        // No query: return most-recently stored entries up to MaxResults
        if (criteria.Query is null)
            return candidates.OrderByDescending(e => e.StoredAt).Take(criteria.MaxResults).ToList();

        // With query: use hybrid ranking if embeddings available, else BM25-only.
        if (_embeddingGenerator is not null)
        {
            try
            {
                var queryText = criteria.Query.Length > _maxEmbeddingInputChars
                    ? criteria.Query[.._maxEmbeddingInputChars]
                    : criteria.Query;
                var queryResult = await _embeddingGenerator.GenerateAsync(queryText);
                var queryEmbedding = queryResult.Vector.ToArray();

                return HybridRanker.Rank(
                        candidates, GetDocumentText,
                        static e => e.Key,
                        e => _embeddings.GetValueOrDefault(e.Key),
                        queryEmbedding, criteria.Query,
                        _minSimilarity)
                    .Take(criteria.MaxResults)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hybrid search failed for working memory — falling back to BM25");
            }
        }

        return Bm25Ranker.Rank(candidates, GetDocumentText, criteria.Query)
            .Take(criteria.MaxResults)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool MatchesPrefix(string key, string? prefix) =>
        string.IsNullOrEmpty(prefix) ||
        key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static string GetDocumentText(WorkingMemoryEntry entry)
    {
        var parts = new List<string>
        {
            entry.Key.Replace('_', ' ').Replace('-', ' ').Replace('/', ' '),
            entry.Value
        };
        if (entry.Tags is { Count: > 0 })
            parts.Add(string.Join(" ", entry.Tags));
        if (entry.Category is not null)
            parts.Add(entry.Category.Replace('/', ' ').Replace('-', ' '));
        return string.Join(" ", parts);
    }

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
