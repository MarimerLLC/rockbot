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
            return Bm25Ranker.Rank(candidates, GetDocumentText, criteria.Query)
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

    // ── BM25 document text ────────────────────────────────────────────────────

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
}
