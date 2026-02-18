using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based long-term memory store with category subdirectories and in-memory index.
/// Thread safety via <see cref="SemaphoreSlim"/> for all file I/O.
/// </summary>
internal sealed partial class FileMemoryStore : ILongTermMemory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _basePath;
    private readonly ILogger<FileMemoryStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Lazy-loaded in-memory index: id -> MemoryEntry
    private Dictionary<string, MemoryEntry>? _index;

    public FileMemoryStore(
        IOptions<MemoryOptions> memoryOptions,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileMemoryStore> logger)
    {
        _basePath = ResolvePath(memoryOptions.Value.BasePath, profileOptions.Value.BasePath);
        _logger = logger;

        Directory.CreateDirectory(_basePath);

        logger.LogInformation("Long-term memory path: {Path}", _basePath);
    }

    public async Task SaveAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ValidateCategory(entry.Category);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken);
            var filePath = GetFilePath(entry.Id, entry.Category);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            // If overwriting, remove old file if category changed
            if (index.TryGetValue(entry.Id, out var existing) && existing.Category != entry.Category)
            {
                var oldPath = GetFilePath(existing.Id, existing.Category);
                if (File.Exists(oldPath))
                    File.Delete(oldPath);
            }

            index[entry.Id] = entry;

            _logger.LogDebug("Saved memory entry {Id} in category {Category}", entry.Id, entry.Category ?? "(none)");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(MemorySearchCriteria criteria, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken);

            // Apply hard filters: category prefix, tags, date range
            var candidates = index.Values
                .Where(e => PassesStructuralFilters(e, criteria))
                .ToList();

            // No query: return most-recently updated entries up to MaxResults
            if (criteria.Query is null)
            {
                return candidates
                    .OrderByDescending(e => e.UpdatedAt ?? e.CreatedAt)
                    .Take(criteria.MaxResults)
                    .ToList();
            }

            // With query: BM25 ranking — entries are scored, zero-score entries excluded,
            // results returned in descending relevance order.
            return RankByBm25(candidates, criteria.Query)
                .Take(criteria.MaxResults)
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<MemoryEntry?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken);
            return index.GetValueOrDefault(id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken);

            if (!index.TryGetValue(id, out var entry))
                return;

            var filePath = GetFilePath(id, entry.Category);
            if (File.Exists(filePath))
                File.Delete(filePath);

            index.Remove(id);

            _logger.LogDebug("Deleted memory entry {Id}", id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken);
            return index.Values
                .SelectMany(e => e.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync(cancellationToken);
            return index.Values
                .Select(e => e.Category)
                .Where(c => c is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── BM25 Ranking ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <paramref name="candidates"/> ordered by BM25 relevance against <paramref name="query"/>.
    /// Entries with no matching terms (score = 0) are excluded from results.
    /// </summary>
    /// <remarks>
    /// Uses Okapi BM25 with k1=1.5 and b=0.75 (standard production defaults).
    /// Single-word terms are scored against tokenized document text (content + tags + category).
    /// Consecutive two-word query phrases receive 2× weight to reward adjacent term matches.
    /// Document frequencies are precomputed to avoid O(N²) inner loops.
    /// </remarks>
    internal static IReadOnlyList<MemoryEntry> RankByBm25(
        IReadOnlyList<MemoryEntry> candidates, string query,
        double k1 = 1.5, double b = 0.75)
    {
        if (candidates.Count == 0) return [];

        var queryTokens = Tokenize(query)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (queryTokens.Length == 0) return [];

        var queryPhrases = GetTwoWordPhrases(Tokenize(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Build per-document token sets once
        var docs = candidates.Select(e =>
        {
            var text = GetDocumentText(e);
            return (Entry: e, Text: text, Tokens: Tokenize(text));
        }).ToArray();

        int N = docs.Length;
        double avgdl = docs.Average(d => (double)d.Tokens.Length);
        if (avgdl == 0) avgdl = 1;

        // Precompute document frequencies (how many docs contain each term/phrase)
        var termDf = queryTokens.ToDictionary(
            t => t,
            t => docs.Count(d => d.Tokens.Any(tok => tok.Equals(t, StringComparison.OrdinalIgnoreCase))),
            StringComparer.OrdinalIgnoreCase);

        var phraseDf = queryPhrases.ToDictionary(
            p => p,
            p => docs.Count(d => d.Text.Contains(p, StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

        return docs
            .Select(doc =>
            {
                var score = 0.0;
                int len = doc.Tokens.Length;
                var freq = doc.Tokens
                    .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                // Single-word BM25 scores
                foreach (var term in queryTokens)
                {
                    int n = termDf[term];
                    if (n == 0) continue;

                    int f = freq.GetValueOrDefault(term, 0);
                    if (f == 0) continue;

                    double idf = Math.Log((N - n + 0.5) / (n + 0.5) + 1.0);
                    double tf = f * (k1 + 1) / (f + k1 * (1 - b + b * len / avgdl));
                    score += idf * tf;
                }

                // Two-word phrase bonus — 2× weight for adjacent term matches
                foreach (var phrase in queryPhrases)
                {
                    int n = phraseDf[phrase];
                    if (n == 0) continue;

                    int f = CountPhraseOccurrences(doc.Text, phrase);
                    if (f == 0) continue;

                    double idf = Math.Log((N - n + 0.5) / (n + 0.5) + 1.0);
                    double tf = f * (k1 + 1) / (f + k1 * (1 - b + b * len / avgdl));
                    score += 2.0 * idf * tf;
                }

                return (doc.Entry, Score: score);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Select(r => r.Entry)
            .ToList();
    }

    /// <summary>
    /// Returns the text used as the BM25 document for a memory entry:
    /// content + space-separated tags + category (slashes and hyphens replaced with spaces).
    /// </summary>
    internal static string GetDocumentText(MemoryEntry entry)
    {
        var parts = new List<string> { entry.Content };
        if (entry.Tags.Count > 0)
            parts.Add(string.Join(" ", entry.Tags));
        if (entry.Category is not null)
            parts.Add(entry.Category.Replace('/', ' ').Replace('-', ' '));
        return string.Join(" ", parts);
    }

    /// <summary>Splits <paramref name="text"/> into lowercase tokens of 3+ characters.</summary>
    internal static string[] Tokenize(string text) =>
        TokenizerPattern().Split(text.ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .ToArray();

    /// <summary>Returns all consecutive two-word phrases from a token array.</summary>
    private static string[] GetTwoWordPhrases(string[] tokens) =>
        tokens.Length < 2
            ? []
            : Enumerable.Range(0, tokens.Length - 1)
                .Select(i => $"{tokens[i]} {tokens[i + 1]}")
                .ToArray();

    /// <summary>Counts case-insensitive non-overlapping occurrences of <paramref name="phrase"/> in <paramref name="text"/>.</summary>
    private static int CountPhraseOccurrences(string text, string phrase)
    {
        int count = 0, start = 0;
        while (true)
        {
            int idx = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;
            count++;
            start = idx + phrase.Length; // non-overlapping
        }
        return count;
    }

    // ── Structural Filter ─────────────────────────────────────────────────────

    private static bool PassesStructuralFilters(MemoryEntry entry, MemorySearchCriteria criteria)
    {
        if (criteria.Category is not null)
        {
            if (entry.Category is null) return false;

            // Prefix match: "project-context" matches "project-context" and "project-context/rockbot"
            if (!entry.Category.Equals(criteria.Category, StringComparison.OrdinalIgnoreCase) &&
                !entry.Category.StartsWith(criteria.Category + "/", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (criteria.Tags is { Count: > 0 })
        {
            if (!criteria.Tags.All(tag =>
                    entry.Tags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase))))
                return false;
        }

        if (criteria.CreatedAfter.HasValue && entry.CreatedAt < criteria.CreatedAfter.Value) return false;
        if (criteria.CreatedBefore.HasValue && entry.CreatedAt > criteria.CreatedBefore.Value) return false;

        return true;
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private async Task<Dictionary<string, MemoryEntry>> EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_index is not null)
            return _index;

        _index = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_basePath))
            return _index;

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var entry = JsonSerializer.Deserialize<MemoryEntry>(json, JsonOptions);
                if (entry is not null)
                {
                    _index[entry.Id] = entry;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed memory file: {Path}", file);
            }
        }

        _logger.LogDebug("Loaded {Count} memory entries from {Path}", _index.Count, _basePath);
        return _index;
    }

    private string GetFilePath(string id, string? category)
    {
        if (category is not null)
            return Path.Combine(_basePath, category, $"{id}.json");

        return Path.Combine(_basePath, $"{id}.json");
    }

    internal static string ResolvePath(string memoryBasePath, string profileBasePath)
    {
        if (Path.IsPathRooted(memoryBasePath))
            return memoryBasePath;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, memoryBasePath);
    }

    internal static void ValidateCategory(string? category)
    {
        if (category is null)
            return;

        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Category cannot be empty or whitespace.", nameof(category));

        if (Path.IsPathRooted(category))
            throw new ArgumentException($"Category must be a relative path: '{category}'", nameof(category));

        if (category.Contains(".."))
            throw new ArgumentException($"Category cannot contain '..': '{category}'", nameof(category));

        if (!CategoryPattern().IsMatch(category))
            throw new ArgumentException(
                $"Category contains invalid characters: '{category}'. Only alphanumeric, hyphens, underscores, and '/' are allowed.",
                nameof(category));
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_\-]+(/[a-zA-Z0-9_\-]+)*$")]
    private static partial Regex CategoryPattern();

    /// <summary>Splits on any sequence of characters that are not lowercase letters or digits.</summary>
    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex TokenizerPattern();
}
