using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based saved-response store. Each entry is persisted as a JSON file at
/// <c>{basePath}/{id}.json</c>. Thread safety via <see cref="SemaphoreSlim"/>.
/// </summary>
internal sealed class FileSavedResponseStore : ISavedResponseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _basePath;
    private readonly ILogger<FileSavedResponseStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Lazy-loaded in-memory index: id -> SavedResponse
    private Dictionary<string, SavedResponse>? _index;

    public FileSavedResponseStore(
        IOptions<SavedResponseOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileSavedResponseStore> logger)
    {
        _basePath = ResolvePath(options.Value.BasePath, profileOptions.Value.BasePath);
        _logger = logger;

        Directory.CreateDirectory(_basePath);
        logger.LogInformation("Saved-response store path: {Path}", _basePath);
    }

    public async Task SaveAsync(SavedResponse response, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync();
            var filePath = GetFilePath(response.Id);

            var json = JsonSerializer.Serialize(response, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            index[response.Id] = response;

            _logger.LogDebug("Saved response '{Id}' with label '{Label}'", response.Id, response.Label);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<SavedResponse?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync();
            return index.GetValueOrDefault(id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<SavedResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync();
            return index.Values
                .OrderByDescending(r => r.SavedAt)
                .ToList();
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
            var index = await EnsureIndexAsync();

            if (!index.Remove(id))
                return;

            var filePath = GetFilePath(id);
            if (File.Exists(filePath))
                File.Delete(filePath);

            _logger.LogDebug("Deleted saved response '{Id}'", id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Infrastructure ────────────────────────────────────────────────────────

    private async Task<Dictionary<string, SavedResponse>> EnsureIndexAsync()
    {
        if (_index is not null)
            return _index;

        _index = new Dictionary<string, SavedResponse>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_basePath))
            return _index;

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var response = JsonSerializer.Deserialize<SavedResponse>(json, JsonOptions);
                if (response is not null)
                    _index[response.Id] = response;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping malformed saved-response file: {Path}", file);
            }
        }

        _logger.LogDebug("Loaded {Count} saved responses from {Path}", _index.Count, _basePath);
        return _index;
    }

    private string GetFilePath(string id) => Path.Combine(_basePath, id + ".json");

    internal static string ResolvePath(string storePath, string profileBasePath)
    {
        if (Path.IsPathRooted(storePath))
            return storePath;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, storePath);
    }
}
