using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based saved-response store. Each entry is persisted as a JSON file at
/// <c>{basePath}/{id}.json</c>. Thread safety via <see cref="SemaphoreSlim"/>.
/// </summary>
/// <remarks>
/// Self-maintaining (#418): content is capped at <see cref="SavedResponseOptions.MaxContentChars"/>,
/// and the limits in <see cref="SavedResponseOptions"/> are applied when the index is loaded (so a
/// lowered limit takes effect on restart) and after every save. No background job or manual cleanup
/// is needed.
/// </remarks>
internal sealed class FileSavedResponseStore : ISavedResponseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _basePath;
    private readonly SavedResponseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FileSavedResponseStore> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // Lazy-loaded in-memory index: id -> SavedResponse
    private Dictionary<string, SavedResponse>? _index;

    public FileSavedResponseStore(
        IOptions<SavedResponseOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileSavedResponseStore> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _basePath = ResolvePath(_options.BasePath, profileOptions.Value.BasePath);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;

        Directory.CreateDirectory(_basePath);
        logger.LogInformation("Saved-response store path: {Path}", _basePath);
    }

    public async Task SaveAsync(SavedResponse response, CancellationToken cancellationToken = default)
    {
        response = CapContent(response, _options.MaxContentChars);

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var index = await EnsureIndexAsync();
            var filePath = GetFilePath(response.Id);

            var json = JsonSerializer.Serialize(response, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            index[response.Id] = response;

            _logger.LogDebug("Saved response '{Id}' with label '{Label}'", response.Id, response.Label);

            Prune(index);
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

            DeleteFile(id);

            _logger.LogDebug("Deleted saved response '{Id}'", id);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // ── Retention ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Cuts content past <paramref name="maxChars"/> and says so at the end, so a reader knows the
    /// entry is incomplete rather than assuming the report just stopped.
    /// </summary>
    internal static SavedResponse CapContent(SavedResponse response, int maxChars)
    {
        if (maxChars <= 0 || response.Content.Length <= maxChars)
            return response;

        var note = $"\n\n[Truncated: the original was {response.Content.Length:N0} characters; " +
                   $"saved responses keep the first {maxChars:N0}.]";
        return response with { Content = string.Concat(response.Content.AsSpan(0, maxChars), note) };
    }

    /// <summary>
    /// The entries the limits in <paramref name="options"/> remove, given everything currently
    /// stored. Automatic entries go by age, then per-label count (scheduled reports), then total count; explicit
    /// entries only by total count. Newest are always the ones kept.
    /// </summary>
    internal static IReadOnlyList<SavedResponse> SelectForPruning(
        IEnumerable<SavedResponse> entries, SavedResponseOptions options, DateTimeOffset now)
    {
        var all = entries.ToList();
        var remove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var auto = all.Where(r => r.AutoSaved).OrderByDescending(r => r.SavedAt).ToList();

        if (options.AutoSavedRetention > TimeSpan.Zero)
        {
            var cutoff = now - options.AutoSavedRetention;
            foreach (var r in auto.Where(r => r.SavedAt < cutoff))
                remove.Add(r.Id);
        }

        if (options.MaxScheduledRunsPerTask > 0)
        {
            // Only scheduled reports repeat under one label; distinct one-off results that happen
            // to share a label (two runs of the same MCP tool) must not prune each other.
            foreach (var group in auto.Where(r => !remove.Contains(r.Id) && r.Source == SavedResponseSources.Scheduled)
                         .GroupBy(r => r.Label.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var r in group.Skip(options.MaxScheduledRunsPerTask))
                    remove.Add(r.Id);
            }
        }

        if (options.MaxAutoSaved > 0)
        {
            foreach (var r in auto.Where(r => !remove.Contains(r.Id)).Skip(options.MaxAutoSaved))
                remove.Add(r.Id);
        }

        if (options.MaxExplicitSaved > 0)
        {
            foreach (var r in all.Where(r => !r.AutoSaved).OrderByDescending(r => r.SavedAt)
                         .Skip(options.MaxExplicitSaved))
                remove.Add(r.Id);
        }

        return all.Where(r => remove.Contains(r.Id)).ToList();
    }

    /// <summary>Applies <see cref="SelectForPruning"/> to the index and the files. Call under the lock.</summary>
    private void Prune(Dictionary<string, SavedResponse> index)
    {
        var pruned = SelectForPruning(index.Values, _options, _time.GetUtcNow());
        if (pruned.Count == 0)
            return;

        foreach (var r in pruned)
        {
            index.Remove(r.Id);
            DeleteFile(r.Id);
        }

        _logger.LogInformation(
            "Pruned {Count} saved response(s) past the retention limits ({Remaining} remain)",
            pruned.Count, index.Count);
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

        // A limit lowered since the last run, or entries that aged out while the agent was down.
        Prune(_index);
        return _index;
    }

    private void DeleteFile(string id)
    {
        var filePath = GetFilePath(id);
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException ex)
        {
            // The index no longer holds it; a leftover file is reloaded and pruned again next start.
            _logger.LogWarning(ex, "Could not delete saved-response file {Path}", filePath);
        }
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
