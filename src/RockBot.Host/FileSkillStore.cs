using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based skill store. Each skill is persisted as a JSON file at
/// <c>{basePath}/{name}.json</c>, where the name may contain forward slashes
/// to form subcategories (e.g. <c>research/summarize-paper</c>).
/// Sub-resource files for a skill named <c>myskill</c> live in the sibling
/// folder <c>{basePath}/myskill/</c>.
/// Thread safety via <see cref="SemaphoreSlim"/>.
/// </summary>
internal sealed partial class FileSkillStore : ISkillStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _basePath;
    private readonly ILogger<FileSkillStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly EmbeddingCache? _embeddingCache;
    private readonly float _minSimilarity;

    // Lazy-loaded in-memory index: name -> Skill
    private Dictionary<string, Skill>? _index;

    public FileSkillStore(
        IOptions<SkillOptions> skillOptions,
        IOptions<AgentProfileOptions> profileOptions,
        IOptions<EmbeddingOptions> embeddingOptions,
        ILogger<FileSkillStore> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        _basePath = ResolvePath(skillOptions.Value.BasePath, profileOptions.Value.BasePath);
        _logger = logger;
        _embeddingCache = embeddingGenerator is not null
            ? new EmbeddingCache(embeddingGenerator, _basePath, logger, embeddingOptions.Value.MaxInputChars)
            : null;
        _minSimilarity = embeddingOptions.Value.MinSimilarityThreshold;

        Directory.CreateDirectory(_basePath);
        logger.LogInformation("Skill store path: {Path} (hybrid search: {Hybrid})",
            _basePath, _embeddingCache is not null);
    }

    public async Task SaveAsync(Skill skill)
    {
        ValidateName(skill.Name);

        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();
            var filePath = GetFilePath(skill.Name);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var json = JsonSerializer.Serialize(skill, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);

            index[skill.Name] = skill;

            _logger.LogDebug("Saved skill '{Name}'", skill.Name);
        }
        finally
        {
            _semaphore.Release();
        }

        // Generate embedding in the background — agent flow should not block on vectorization.
        if (_embeddingCache is not null)
            _ = _embeddingCache.UpdateAsync(skill.Name, GetDocumentText(skill));
    }

    public async Task SaveAsync(Skill skill, IReadOnlyList<SkillResourceInput>? resources)
    {
        if (resources is null || resources.Count == 0)
        {
            await SaveAsync(skill);
            return;
        }

        ValidateName(skill.Name);
        foreach (var r in resources)
            ValidateFilename(r.Filename);

        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();
            var filePath = GetFilePath(skill.Name);
            var folderPath = GetResourceFolderPath(skill.Name);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            Directory.CreateDirectory(folderPath);

            // Write new resource files
            var newFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in resources)
            {
                newFilenames.Add(resource.Filename);
                var resourcePath = Path.Combine(folderPath, resource.Filename);
                await File.WriteAllTextAsync(resourcePath, resource.Content);
            }

            // Prune orphaned files from the folder that are not in the new bundle
            foreach (var existingFile in Directory.EnumerateFiles(folderPath))
            {
                var name = Path.GetFileName(existingFile);
                if (!newFilenames.Contains(name))
                {
                    File.Delete(existingFile);
                    _logger.LogDebug("Pruned orphaned resource '{File}' from skill '{Name}'", name, skill.Name);
                }
            }

            // Build manifest from the provided resources
            var manifest = resources
                .Select(r => new SkillResource(r.Filename, r.Type, r.Description))
                .ToList();

            // Save skill JSON with updated manifest
            var skillWithManifest = skill with { Manifest = manifest };
            var json = JsonSerializer.Serialize(skillWithManifest, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);

            index[skill.Name] = skillWithManifest;

            _logger.LogDebug("Saved skill '{Name}' with {Count} resource(s)", skill.Name, manifest.Count);
        }
        finally
        {
            _semaphore.Release();
        }

        // Generate embedding in the background
        if (_embeddingCache is not null)
            _ = _embeddingCache.UpdateAsync(skill.Name, GetDocumentText(skill));
    }

    public async Task<Skill?> GetAsync(string name)
    {
        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();
            return index.GetValueOrDefault(name);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<string?> GetResourceAsync(string skillName, string filename)
    {
        ValidateName(skillName);
        ValidateFilename(filename);

        await _semaphore.WaitAsync();
        try
        {
            var folderPath = GetResourceFolderPath(skillName);
            var filePath = Path.Combine(folderPath, filename);

            // Security: ensure the resolved path stays within the resource folder
            var resolvedFolder = Path.GetFullPath(folderPath);
            var resolvedFile = Path.GetFullPath(filePath);
            if (!resolvedFile.StartsWith(resolvedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Resource filename would escape the skill folder: '{filename}'", nameof(filename));

            if (!File.Exists(filePath))
                return null;

            return await File.ReadAllTextAsync(filePath);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<Skill>> ListAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();
            return index.Values
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task DeleteAsync(string name)
    {
        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();

            if (!index.Remove(name, out var skill))
                return;

            var filePath = GetFilePath(name);
            if (File.Exists(filePath))
                File.Delete(filePath);

            // Delete the resource subfolder if it exists
            var folderPath = GetResourceFolderPath(name);
            if (Directory.Exists(folderPath))
                Directory.Delete(folderPath, recursive: true);

            _embeddingCache?.Remove(name);

            _logger.LogDebug("Deleted skill '{Name}'", skill.Name);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<Skill>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken = default, float[]? queryEmbedding = null)
    {
        // Only hold the semaphore long enough to snapshot the index
        List<Skill> candidates;
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync();
            candidates = index.Values.ToList();
        }
        finally
        {
            _semaphore.Release();
        }

        if (_embeddingCache is not null)
        {
            using var hybridActivity = HostDiagnostics.Source.StartActivity("rockbot.search.hybrid.skills");
            var sw = Stopwatch.StartNew();

            // Use pre-computed query embedding if provided, otherwise generate one
            queryEmbedding ??= await _embeddingCache.GenerateQueryEmbeddingAsync(query, cancellationToken);
            if (queryEmbedding is not null)
            {
                // Batch-load all candidate embeddings (single endpoint call for cache misses)
                var batchItems = candidates
                    .Select(s => (s.Name, Text: GetDocumentText(s)))
                    .ToList();
                var embeddingMap = await _embeddingCache.GetOrCreateBatchAsync(batchItems, cancellationToken);

                var results = HybridRanker.Rank(
                        candidates, GetDocumentText,
                        static s => s.Name,
                        s => embeddingMap.GetValueOrDefault(s.Name),
                        queryEmbedding, query,
                        _minSimilarity)
                    .Take(maxResults)
                    .ToList();

                sw.Stop();
                HostDiagnostics.HybridSearchDuration.Record(sw.Elapsed.TotalMilliseconds);
                _logger.LogInformation("Hybrid skill search completed in {Duration:F0}ms ({Candidates} candidates, {Results} results)",
                    sw.Elapsed.TotalMilliseconds, candidates.Count, results.Count);
                return results;
            }
        }

        return Bm25Ranker.Rank(candidates, GetDocumentText, query)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Returns the text used as the BM25 document for a skill:
    /// name (hyphens replaced with spaces) + summary.
    /// </summary>
    internal static string GetDocumentText(Skill skill)
    {
        var namePart = skill.Name.Replace('/', ' ').Replace('-', ' ');
        if (string.IsNullOrWhiteSpace(skill.Summary))
            return namePart;
        return $"{namePart} {skill.Summary}";
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private async Task<Dictionary<string, Skill>> EnsureIndexAsync()
    {
        if (_index is not null)
            return _index;

        _index = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_basePath))
            return _index;

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories))
        {
            // Skip JSON files that live inside a skill's resource subfolder.
            // A resource subfolder is identified by having a sibling .json file with the same name.
            if (IsInsideResourceSubfolder(file))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(file);
                var skill = JsonSerializer.Deserialize<Skill>(json, JsonOptions);
                if (skill is not null)
                    _index[skill.Name] = skill;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed skill file: {Path}", file);
            }
        }

        _logger.LogDebug("Loaded {Count} skills from {Path}", _index.Count, _basePath);
        return _index;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="filePath"/> lives inside a skill's resource subfolder
    /// (i.e., the file's immediate parent directory has a sibling <c>.json</c> entry-point file).
    /// </summary>
    private bool IsInsideResourceSubfolder(string filePath)
    {
        var parentDir = Path.GetDirectoryName(filePath);
        if (parentDir is null)
            return false;

        // Top-level files are always skill entry points
        if (string.Equals(Path.GetFullPath(parentDir), Path.GetFullPath(_basePath), StringComparison.OrdinalIgnoreCase))
            return false;

        // If there is a .json file with the same name as the parent directory, the parent is a resource subfolder
        var siblingJson = parentDir.TrimEnd(Path.DirectorySeparatorChar) + ".json";
        return File.Exists(siblingJson);
    }

    private string GetFilePath(string name) =>
        Path.Combine(_basePath, name.Replace('/', Path.DirectorySeparatorChar) + ".json");

    /// <summary>
    /// Returns the path of the resource subfolder for a skill.
    /// For a skill named <c>myskill</c> the folder is <c>{basePath}/myskill/</c>;
    /// for <c>research/summarize</c> it is <c>{basePath}/research/summarize/</c>.
    /// </summary>
    private string GetResourceFolderPath(string name) =>
        Path.Combine(_basePath, name.Replace('/', Path.DirectorySeparatorChar));

    internal static string ResolvePath(string skillBasePath, string profileBasePath)
    {
        if (Path.IsPathRooted(skillBasePath))
            return skillBasePath;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, skillBasePath);
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Skill name cannot be empty or whitespace.", nameof(name));

        if (Path.IsPathRooted(name))
            throw new ArgumentException($"Skill name must be a relative path: '{name}'", nameof(name));

        if (name.Contains(".."))
            throw new ArgumentException($"Skill name cannot contain '..': '{name}'", nameof(name));

        if (!NamePattern().IsMatch(name))
            throw new ArgumentException(
                $"Skill name contains invalid characters: '{name}'. " +
                "Only alphanumeric, hyphens, underscores, and '/' are allowed.",
                nameof(name));
    }

    internal static void ValidateFilename(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            throw new ArgumentException("Resource filename cannot be empty.", nameof(filename));

        if (filename.Contains('/') || filename.Contains('\\'))
            throw new ArgumentException(
                $"Resource filename cannot contain path separators: '{filename}'",
                nameof(filename));

        if (filename.Contains(".."))
            throw new ArgumentException(
                $"Resource filename cannot contain '..': '{filename}'",
                nameof(filename));

        if (!FilenamePattern().IsMatch(filename))
            throw new ArgumentException(
                $"Resource filename contains invalid characters: '{filename}'. " +
                "Only alphanumeric, hyphens, underscores, and dots are allowed.",
                nameof(filename));
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_\-]+(/[a-zA-Z0-9_\-]+)*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]+$")]
    private static partial Regex FilenamePattern();
}
