using System.ClientModel;
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
internal sealed partial class HybridCacheWorkingMemory : IWorkingMemory
{
    private readonly IMemoryCache _cache;
    private readonly WorkingMemoryOptions _options;
    private readonly ILogger<HybridCacheWorkingMemory> _logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly EmbeddingTextPreparer _preparer;
    private readonly float _minSimilarity;

    // fullKey -> EntryMeta
    private readonly ConcurrentDictionary<string, EntryMeta> _index = new(StringComparer.OrdinalIgnoreCase);

    // fullKey -> float[] (in-memory only, evicted with the entry)
    private readonly ConcurrentDictionary<string, float[]> _embeddings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One lock per key, held across an <see cref="EditAsync"/> read-modify-write so two
    /// concurrent edits to the same entry cannot both start from the pre-edit value. Entries
    /// are never evicted — one small object per distinct key edited in the process lifetime.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _editLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Categories whose entries are regenerable — see <see cref="IsRegenerable"/>.</summary>
    private readonly HashSet<string> _regenerableCategories;

    /// <summary>
    /// A class rather than a record: it carries mutable read recency, which must not take part
    /// in value equality or be copied by a <c>with</c> expression.
    /// </summary>
    private sealed class EntryMeta(
        DateTimeOffset storedAt,
        DateTimeOffset expiresAt,
        string? category,
        IReadOnlyList<string> tags,
        string? writer = null)
    {
        private long _lastReadTicks;

        public DateTimeOffset StoredAt { get; } = storedAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string? Category { get; } = category;
        public IReadOnlyList<string> Tags { get; } = tags;
        public string? Writer { get; } = writer;

        /// <summary>UTC ticks of the last <see cref="TouchAsync"/> (a model read); 0 when never read.</summary>
        public long LastReadTicks => Interlocked.Read(ref _lastReadTicks);

        public void MarkRead(long utcTicks) => Interlocked.Exchange(ref _lastReadTicks, utcTicks);

        /// <summary>When the entry was last written or read, whichever is later (#803).</summary>
        public long LastUsedTicks => Math.Max(StoredAt.UtcTicks, LastReadTicks);
    }

    public HybridCacheWorkingMemory(
        IMemoryCache cache,
        IOptions<WorkingMemoryOptions> options,
        IOptions<EmbeddingOptions> embeddingOptions,
        EmbeddingTextPreparer preparer,
        ILogger<HybridCacheWorkingMemory> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        _cache = cache;
        _options = options.Value;
        _logger = logger;
        _embeddingGenerator = embeddingGenerator;
        _preparer = preparer;
        _minSimilarity = embeddingOptions.Value.MinSimilarityThreshold;
        _regenerableCategories = new HashSet<string>(_options.EffectiveEvictableCategories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an entry of this category is regenerable content (page chunks, chunked tool
    /// results, chunked wisp input — <see cref="WorkingMemoryOptions.EvictableCategories"/>).
    /// Regenerable entries count against <see cref="WorkingMemoryOptions.MaxRegenerableEntriesPerNamespace"/>
    /// and can be evicted; everything else counts against
    /// <see cref="WorkingMemoryOptions.MaxEntriesPerNamespace"/> and never is (#803).
    /// </summary>
    internal bool IsRegenerable(string? category) =>
        category is not null && _regenerableCategories.Contains(category);

    /// <summary>
    /// Whether <paramref name="key"/> holds a live entry, and its category, without counting as
    /// a read. <see cref="FileWorkingMemory"/> uses it to decide whether a write touches what it
    /// persists.
    /// </summary>
    internal bool TryPeekCategory(string key, out string? category)
    {
        if (_index.TryGetValue(key, out var meta) && meta.ExpiresAt > DateTimeOffset.UtcNow)
        {
            category = meta.Category;
            return true;
        }

        category = null;
        return false;
    }

    /// <summary>
    /// Marks <paramref name="key"/> as just read by the model, so its chunk group is the last to
    /// be evicted (#803). Separate from <see cref="GetAsync"/>: bookkeeping reads (the context
    /// builder ranking page indexes, the trimmer checking a key still exists) would otherwise
    /// make pages the model stopped using look fresh.
    /// </summary>
    public Task TouchAsync(string key)
    {
        if (_index.TryGetValue(key, out var meta))
            meta.MarkRead(DateTimeOffset.UtcNow.UtcTicks);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Whether <paramref name="key"/> is in namespace <paramref name="ns"/> (as
    /// <see cref="GetNamespace"/> would say), without allocating — this runs over the whole
    /// index on every write.
    /// </summary>
    private static bool InNamespace(string key, string ns)
    {
        if (key.Length == ns.Length)
            return key.Equals(ns, StringComparison.OrdinalIgnoreCase);
        // A namespace without '/' is a single-segment key, which is its own namespace.
        return ns.Contains('/')
            && key.Length > ns.Length
            && key[ns.Length] == '/'
            && key.StartsWith(ns, StringComparison.OrdinalIgnoreCase);
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
        => SetAsync(key, value, ttl, category, tags, writer: null);

    public Task SetAsync(string key, string value, TimeSpan? ttl, string? category,
        IReadOnlyList<string>? tags, string? writer)
        => TrySetAsync(key, value, ttl, category, tags, writer);

    public Task<WorkingMemoryWriteResult> TrySetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null, string? writer = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now + (ttl ?? _options.DefaultTtl);
        return Task.FromResult(SetCore(key, value, now, expiresAt, category, tags, writer));
    }

    /// <summary>
    /// Re-seeds an entry loaded from disk, keeping its original stored-at and writer. Going
    /// through <see cref="SetAsync(string, string, TimeSpan?, string?, IReadOnlyList{string}?)"/>
    /// would stamp it with the restart time — which made a days-old patrol snapshot look like
    /// it had just been written (issue #668). A <c>default</c> <paramref name="storedAt"/>
    /// (legacy file with no timestamp) is kept as "unknown".
    /// </summary>
    internal Task RestoreAsync(string key, string value, DateTimeOffset storedAt, DateTimeOffset expiresAt,
        string? category, IReadOnlyList<string>? tags, string? writer)
    {
        SetCore(key, value, storedAt, expiresAt, category, tags, writer);
        return Task.CompletedTask;
    }

    private WorkingMemoryWriteResult SetCore(string key, string value, DateTimeOffset storedAt, DateTimeOffset expiresAt,
        string? category, IReadOnlyList<string>? tags, string? writer)
    {
        var now = DateTimeOffset.UtcNow;
        var effectiveTtl = expiresAt - now;

        // #803: regenerable content (page and result chunks) and deliberate saves have separate
        // budgets, so a research run can't crowd out the agent's own notes.
        var ns = GetNamespace(key);
        var regenerable = IsRegenerable(category);
        var limit = regenerable ? _options.MaxRegenerableEntriesPerNamespace : _options.MaxEntriesPerNamespace;
        var budget = regenerable ? "cached page and result chunks" : "saved entries";

        // Overwriting a key is always allowed, even into the other budget: it adds no entry.
        _index.TryGetValue(key, out var existing);

        List<string>? evicted = null;
        if (existing is null)
        {
            var count = _index.Count(kvp =>
                kvp.Value.ExpiresAt > now &&
                IsRegenerable(kvp.Value.Category) == regenerable &&
                InNamespace(kvp.Key, ns));

            if (count >= limit)
            {
                // #801: make room from other regenerable content before refusing. Deliberate saves
                // are never evicted, and evicting pages wouldn't free one of their slots.
                evicted = regenerable ? EvictForRoom(ns, key, now, needed: count - limit + 1) : [];
                if (evicted.Count == 0)
                {
                    _logger.LogWarning(
                        "Working memory limit reached for namespace '{Namespace}' ({Max} {Budget}) with nothing evictable; refusing key '{Key}'",
                        ns, limit, budget, key);
                    return WorkingMemoryWriteResult.Refused(regenerable
                        ? $"working memory for '{ns}' is full ({limit} {budget}) and holds no other page or result that can be evicted"
                        : $"working memory for '{ns}' is full ({limit} {budget}); delete or overwrite an entry you no longer need");
                }

                _logger.LogInformation(
                    "Working memory limit reached for namespace '{Namespace}' ({Max} {Budget}); evicted {Count} least recently read regenerable entr(ies) for '{Key}'",
                    ns, limit, budget, evicted.Count, key);
            }
        }

        var meta = new EntryMeta(storedAt, expiresAt, category, tags ?? [], writer);
        if (existing is not null)
            meta.MarkRead(existing.LastReadTicks); // an overwrite or edit keeps the read history
        _index[key] = meta;
        _cache.Set(CacheKey(key), value, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = expiresAt
        });

        _logger.LogDebug("Working memory set: key={Key} ttl={Ttl}", key, effectiveTtl);

        // Generate embedding in the background (best-effort, non-blocking).
        // Skip ephemeral exact-key namespaces (wisp, tool-result stash) — see IsEphemeralKey.
        if (_embeddingGenerator is not null && !IsEphemeralKey(key))
        {
            _ = GenerateEmbeddingAsync(key, value, category, tags);
        }

        return evicted is { Count: > 0 } ? new WorkingMemoryWriteResult(true, null, evicted) : WorkingMemoryWriteResult.Ok;
    }

    /// <summary>
    /// Evicts the least recently used regenerable entries in <paramref name="ns"/> (categories
    /// in <see cref="WorkingMemoryOptions.EffectiveEvictableCategories"/>) until at least
    /// <paramref name="needed"/> are gone. A whole chunk group goes at once (every chunk and the
    /// index of one page or result), so nothing is left half there; a group's recency is the
    /// latest write or model read (<see cref="TouchAsync"/>) of any of its entries, so a page the
    /// model keeps reading stays and
    /// one it fetched and never opened goes first (#803). The group <paramref name="incomingKey"/>
    /// belongs to is never a candidate, so a page being written can't evict its own earlier
    /// chunks. Returns the evicted keys; empty when nothing could go.
    /// </summary>
    private List<string> EvictForRoom(string ns, string incomingKey, DateTimeOffset now, int needed)
    {
        var evicted = new List<string>();
        var incomingGroup = ChunkGroup(incomingKey);
        var groups = _index
            .Where(kvp => kvp.Value.ExpiresAt > now
                && IsRegenerable(kvp.Value.Category)
                && InNamespace(kvp.Key, ns))
            .GroupBy(kvp => ChunkGroup(kvp.Key), StringComparer.OrdinalIgnoreCase)
            .Where(g => !g.Key.Equals(incomingGroup, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => g.Max(e => e.Value.LastUsedTicks))
            .ToList(); // snapshot: the loop below removes from _index

        foreach (var group in groups)
        {
            foreach (var entry in group)
            {
                _index.TryRemove(entry.Key, out _);
                _embeddings.TryRemove(entry.Key, out _);
                _cache.Remove(CacheKey(entry.Key));
                evicted.Add(entry.Key);
            }

            if (evicted.Count >= needed)
                break;
        }

        return evicted;
    }

    /// <summary>
    /// The chunk group a key belongs to: the key without a trailing <c>-chunk{n}</c> or
    /// <c>-index</c>, the suffixes <c>web_browse</c>, the chunking wrapper and wisps give the
    /// pieces of one page or result. Any other key is its own group.
    /// </summary>
    internal static string ChunkGroup(string key) => ChunkSuffix().Replace(key, string.Empty);

    [System.Text.RegularExpressions.GeneratedRegex(@"-(chunk\d+|index)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChunkSuffix();

    private async Task GenerateEmbeddingAsync(string key, string value, string? category, IReadOnlyList<string>? tags)
    {
        string? docText = null;
        try
        {
            // The key leads the document text, so classify the value's shape on its own —
            // otherwise a JSON value never looks structured and gets the prose cap (#460).
            docText = _preparer.Prepare(BuildDocumentText(key, value, category, tags),
                diagnosticKey: key, shapeSample: value);
            var result = await _embeddingGenerator!.GenerateAsync(docText);

            // Only store if the entry still exists — prevents orphaned embeddings
            // when the entry was deleted while generation was in flight.
            if (_index.ContainsKey(key))
                _embeddings[key] = result.Vector.ToArray();
        }
        catch (ClientResultException ex)
        {
            // The exception message carries only the error type ("invalid_request_error: "),
            // so surface the response body for the actual reason (#460).
            _logger.LogWarning(ex,
                "Failed to generate working memory embedding for key {Key}: HTTP {Status}, input {Chars} chars, response: {Body}",
                key, ex.Status, docText?.Length, ReadErrorBody(ex));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate working memory embedding for key {Key}", key);
        }
    }

    private static string ReadErrorBody(ClientResultException ex)
    {
        const int maxChars = 500;
        try
        {
            var body = ex.GetRawResponse()?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(body)) return "(empty)";
            return body.Length <= maxChars ? body : body[..maxChars] + "…";
        }
        catch (Exception)
        {
            return "(unreadable)";
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

    public Task<WorkingMemoryEntry?> GetEntryAsync(string key)
    {
        if (!_index.TryGetValue(key, out var meta) || meta.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _index.TryRemove(key, out _);
            _embeddings.TryRemove(key, out _);
            return Task.FromResult<WorkingMemoryEntry?>(null);
        }

        if (!_cache.TryGetValue<string>(CacheKey(key), out var value) || value is null)
            return Task.FromResult<WorkingMemoryEntry?>(null);

        return Task.FromResult<WorkingMemoryEntry?>(
            new WorkingMemoryEntry(key, value, meta.StoredAt, meta.ExpiresAt, meta.Category, meta.Tags, meta.Writer));
    }

    public async Task<ContentEditResult> EditAsync(string key, string oldText, string newText, bool replaceAll = false)
    {
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        // Serializes edits to this key against each other. A concurrent full SetAsync still
        // wins — working memory has no versioning to compare against — but two models
        // amending the same cached payload no longer silently overwrite one another.
        var gate = _editLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!_index.TryGetValue(key, out var meta) || meta.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return ContentEditResult.Failed(
                    $"Working memory entry '{key}' was not found or has expired, so there is nothing to edit.");
            }

            if (!_cache.TryGetValue<string>(CacheKey(key), out var current) || current is null)
            {
                return ContentEditResult.Failed(
                    $"Working memory entry '{key}' is no longer cached — it was evicted under memory pressure. " +
                    "Re-save the value rather than editing it.");
            }

            var edit = TextEdit.Apply(current, oldText, newText, replaceAll);
            if (!edit.IsSuccess)
                return ContentEditResult.Failed(edit.Error!);

            // Reuse the window the entry was stored with, restarting it from now. Going back
            // through SetAsync is what re-arms the cache expiry and re-embeds the new value.
            // A restored legacy entry has no stored-at, so its original window is unknowable;
            // fall back to what is left of it rather than computing from year 0001.
            // The writer is kept: the edit does not say who made it, and the original writer
            // is still the best provenance available.
            var window = meta.StoredAt != default
                ? meta.ExpiresAt - meta.StoredAt
                : meta.ExpiresAt - DateTimeOffset.UtcNow;
            await SetAsync(
                key,
                edit.Content!,
                window > TimeSpan.Zero ? window : null,
                meta.Category,
                meta.Tags,
                meta.Writer);

            _logger.LogDebug(
                "Working memory edit: key={Key} replacements={Count} {Old}->{New} chars",
                key, edit.ReplacementCount, current.Length, edit.Content!.Length);

            return ContentEditResult.Applied(edit.ReplacementCount, current.Length, edit.Content.Length);
        }
        finally
        {
            gate.Release();
        }
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
                entries.Add(new WorkingMemoryEntry(kvp.Key, value!, meta.StoredAt, meta.ExpiresAt, meta.Category, meta.Tags, meta.Writer));
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

        // With query: use hybrid ranking if embeddings available, else BM25-only. When no
        // candidate has a vector (e.g. a stash- or wisp-scoped search) the vector half would
        // score nothing, so skip the query-embedding round trip and go straight to BM25.
        if (_embeddingGenerator is not null && candidates.Any(e => _embeddings.ContainsKey(e.Key)))
        {
            try
            {
                var queryText = _preparer.Prepare(criteria.Query);
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

    /// <summary>
    /// Keys that belong to short-lived, exact-key-lookup namespaces where embedding
    /// generation is unnecessary (and often exceeds the model's token window). BM25 still
    /// ranks them when a query is scoped to one of these namespaces.
    /// <list type="bullet">
    /// <item><c>wisp/</c> — wisp-scoped output (30-60 min TTL), cleaned up on wisp completion.</item>
    /// <item><c>stash/</c> — full originals of trimmed tool results, written only by the loop
    /// and read back by the key the stash registry surfaces. They are by definition the
    /// largest, densest payloads, and embedding them was the main source of 400s and
    /// minute-long embedding calls (#460).</item>
    /// </list>
    /// </summary>
    private static bool IsEphemeralKey(string key) =>
        key.StartsWith("wisp/", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith(ToolResultStash.Tag + "/", StringComparison.OrdinalIgnoreCase);

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
