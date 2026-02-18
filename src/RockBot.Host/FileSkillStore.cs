using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based skill store. Each skill is persisted as a JSON file at
/// <c>{basePath}/{name}.json</c>, where the name may contain forward slashes
/// to form subcategories (e.g. <c>research/summarize-paper</c>).
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

    // Lazy-loaded in-memory index: name -> Skill
    private Dictionary<string, Skill>? _index;

    public FileSkillStore(
        IOptions<SkillOptions> skillOptions,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileSkillStore> logger)
    {
        _basePath = ResolvePath(skillOptions.Value.BasePath, profileOptions.Value.BasePath);
        _logger = logger;

        Directory.CreateDirectory(_basePath);
        logger.LogInformation("Skill store path: {Path}", _basePath);
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

            _logger.LogDebug("Deleted skill '{Name}'", skill.Name);
        }
        finally
        {
            _semaphore.Release();
        }
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

    private string GetFilePath(string name) =>
        Path.Combine(_basePath, name.Replace('/', Path.DirectorySeparatorChar) + ".json");

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

    [GeneratedRegex(@"^[a-zA-Z0-9_\-]+(/[a-zA-Z0-9_\-]+)*$")]
    private static partial Regex NamePattern();
}
