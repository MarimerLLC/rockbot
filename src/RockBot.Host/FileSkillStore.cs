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
/// folder <c>{basePath}/myskill.resources/</c>. The <c>.resources</c> suffix is
/// reserved (skill names cannot contain dots) and unambiguously distinguishes
/// resource folders from subcategory folders, so a top-level skill <c>a</c> and
/// a subcategory skill <c>a/b</c> can coexist without collision.
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
        EmbeddingTextPreparer embeddingTextPreparer,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null)
    {
        _basePath = ResolvePath(skillOptions.Value.BasePath, profileOptions.Value.BasePath);
        _logger = logger;
        _embeddingCache = embeddingGenerator is not null
            ? new EmbeddingCache(embeddingGenerator, _basePath, logger, embeddingTextPreparer)
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

            // Preserve the existing manifest when the caller hasn't provided one.
            // This prevents a plain metadata/markdown update from silently orphaning
            // resource files that are still on disk.
            var existing = index.GetValueOrDefault(skill.Name);
            var skillToSave = skill.Manifest is null && existing?.Manifest is not null
                ? skill with { Manifest = existing.Manifest }
                : skill;

            var filePath = GetFilePath(skill.Name);

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var json = JsonSerializer.Serialize(skillToSave, JsonOptions);
            await File.WriteAllTextAsync(filePath, json);

            index[skill.Name] = skillToSave;

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

            // Write new resource files.
            // Note: folderPath and newFilenames both use OrdinalIgnoreCase for the prune step;
            // on case-sensitive file systems (Linux) ensure filenames are consistently cased.
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

            // Build manifest from the provided resources. Preserve provisional/verify-hint
            // metadata supplied on the input. CreatedAt is set to "now" for new entries
            // (the bulk path nukes the pre-existing manifest, so existing CreatedAt is
            // not preserved here — use AttachResourceAsync for additive single-resource saves).
            var nowStamp = DateTimeOffset.UtcNow;
            var manifest = resources
                .Select(r => new SkillResource(
                    r.Filename,
                    r.Type,
                    r.Description,
                    Provisional: r.Provisional,
                    CreatedAt: nowStamp,
                    VerifyHint: r.VerifyHint,
                    DefinitionHash: ComputeDefinitionHash(r.Content)))
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

    public async Task<bool> AttachResourceAsync(
        string skillName,
        SkillResourceInput resource,
        SkillResource? manifestEntry = null)
    {
        ValidateName(skillName);
        ValidateFilename(resource.Filename);

        Skill saved;
        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();
            if (!index.TryGetValue(skillName, out var existing))
                return false;

            var folderPath = GetResourceFolderPath(skillName);
            Directory.CreateDirectory(folderPath);

            // Write/replace the resource file
            var resourcePath = Path.Combine(folderPath, resource.Filename);
            await File.WriteAllTextAsync(resourcePath, resource.Content);

            // Build the manifest entry — caller-supplied entry wins; otherwise derive from input.
            var entry = manifestEntry ?? new SkillResource(
                resource.Filename,
                resource.Type,
                resource.Description,
                Provisional: resource.Provisional,
                CreatedAt: DateTimeOffset.UtcNow,
                VerifyHint: resource.VerifyHint,
                DefinitionHash: ComputeDefinitionHash(resource.Content));

            // Replace by filename (case-insensitive to match folder semantics) or append.
            var oldManifest = existing.Manifest ?? [];
            var newManifest = new List<SkillResource>(oldManifest.Count + 1);
            var replaced = false;
            foreach (var old in oldManifest)
            {
                if (string.Equals(old.Filename, resource.Filename, StringComparison.OrdinalIgnoreCase))
                {
                    newManifest.Add(entry);
                    replaced = true;
                }
                else
                {
                    newManifest.Add(old);
                }
            }
            if (!replaced)
                newManifest.Add(entry);

            saved = existing with { Manifest = newManifest, UpdatedAt = DateTimeOffset.UtcNow };
            var json = JsonSerializer.Serialize(saved, JsonOptions);
            await File.WriteAllTextAsync(GetFilePath(skillName), json);
            index[skillName] = saved;

            _logger.LogDebug(
                "Attached resource '{File}' to skill '{Name}' (provisional={Provisional})",
                resource.Filename, skillName, entry.Provisional);
        }
        finally
        {
            _semaphore.Release();
        }

        if (_embeddingCache is not null)
            _ = _embeddingCache.UpdateAsync(saved.Name, GetDocumentText(saved));

        return true;
    }

    public async Task<bool> RemoveResourceAsync(string skillName, string filename)
    {
        ValidateName(skillName);
        ValidateFilename(filename);

        Skill saved;
        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();
            if (!index.TryGetValue(skillName, out var existing) || existing.Manifest is null)
                return false;

            var oldManifest = existing.Manifest;
            var newManifest = oldManifest
                .Where(r => !string.Equals(r.Filename, filename, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (newManifest.Count == oldManifest.Count)
                return false;  // no entry matched

            // Delete the file from disk
            var folderPath = GetResourceFolderPath(skillName);
            var resourcePath = Path.Combine(folderPath, filename);
            if (File.Exists(resourcePath))
                File.Delete(resourcePath);

            saved = existing with
            {
                Manifest = newManifest.Count == 0 ? null : newManifest,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var json = JsonSerializer.Serialize(saved, JsonOptions);
            await File.WriteAllTextAsync(GetFilePath(skillName), json);
            index[skillName] = saved;

            _logger.LogDebug("Removed resource '{File}' from skill '{Name}'", filename, skillName);
        }
        finally
        {
            _semaphore.Release();
        }

        if (_embeddingCache is not null)
            _ = _embeddingCache.UpdateAsync(saved.Name, GetDocumentText(saved));

        return true;
    }

    public async Task<bool> UpdateResourceMetadataAsync(string skillName, SkillResource updated)
    {
        ValidateName(skillName);
        ValidateFilename(updated.Filename);

        Skill saved;
        await _semaphore.WaitAsync();
        try
        {
            var index = await EnsureIndexAsync();
            if (!index.TryGetValue(skillName, out var existing) || existing.Manifest is null)
                return false;

            var oldManifest = existing.Manifest;
            var newManifest = new List<SkillResource>(oldManifest.Count);
            var matched = false;
            foreach (var old in oldManifest)
            {
                if (string.Equals(old.Filename, updated.Filename, StringComparison.OrdinalIgnoreCase))
                {
                    newManifest.Add(updated);
                    matched = true;
                }
                else
                {
                    newManifest.Add(old);
                }
            }

            if (!matched)
                return false;

            saved = existing with { Manifest = newManifest, UpdatedAt = DateTimeOffset.UtcNow };
            var json = JsonSerializer.Serialize(saved, JsonOptions);
            await File.WriteAllTextAsync(GetFilePath(skillName), json);
            index[skillName] = saved;
        }
        finally
        {
            _semaphore.Release();
        }

        if (_embeddingCache is not null)
            _ = _embeddingCache.UpdateAsync(saved.Name, GetDocumentText(saved));

        return true;
    }

    /// <summary>
    /// SHA-256-hex16 of <paramref name="content"/> — same scheme as
    /// <c>SpawnWispsExecutor.ComputeDefinitionHash</c>, kept here to avoid taking a
    /// project dependency on RockBot.Wisp from the Host layer.
    /// </summary>
    internal static string ComputeDefinitionHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    public Task<string?> GetResourceAsync(string skillName, string filename)
    {
        ValidateName(skillName);
        ValidateFilename(filename);

        var folderPath = GetResourceFolderPath(skillName);
        var filePath = Path.Combine(folderPath, filename);

        // Security: ensure the resolved file stays within the resource folder.
        // ValidateFilename already rejects separators and '..' — this is a belt-and-suspenders check.
        var resolvedFolder = Path.GetFullPath(folderPath);
        var resolvedFile = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(resolvedFolder, resolvedFile);
        if (relative.StartsWith("..") || Path.IsPathRooted(relative))
            throw new ArgumentException($"Resource filename would escape the skill folder: '{filename}'", nameof(filename));

        if (!File.Exists(filePath))
            return Task.FromResult<string?>(null);

        return ReadResourceFileAsync(filePath);

        static async Task<string?> ReadResourceFileAsync(string path) =>
            await File.ReadAllTextAsync(path);
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
            // Skip JSON files that live inside a skill's resource folder.
            // Resource folders are suffixed with ".resources" — skill names cannot
            // contain dots, so there is no ambiguity with subcategory folders.
            if (IsInsideResourceFolder(file))
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
    /// Returns <c>true</c> if <paramref name="filePath"/> lives inside a skill's resource folder,
    /// identified by the reserved <c>.resources</c> suffix on any path segment below
    /// <see cref="_basePath"/>.
    /// </summary>
    private static bool IsInsideResourceFolder(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        return dir is not null
            && dir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.EndsWith(ResourceFolderSuffix, StringComparison.Ordinal));
    }

    private string GetFilePath(string name) =>
        Path.Combine(_basePath, name.Replace('/', Path.DirectorySeparatorChar) + ".json");

    /// <summary>
    /// Returns the path of the resource folder for a skill.
    /// For a skill named <c>myskill</c> the folder is <c>{basePath}/myskill.resources/</c>;
    /// for <c>research/summarize</c> it is <c>{basePath}/research/summarize.resources/</c>.
    /// The <c>.resources</c> suffix is reserved — skill names cannot contain dots — so
    /// the folder never collides with a subcategory folder.
    /// </summary>
    private string GetResourceFolderPath(string name) =>
        Path.Combine(_basePath, name.Replace('/', Path.DirectorySeparatorChar) + ResourceFolderSuffix);

    private const string ResourceFolderSuffix = ".resources";

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
