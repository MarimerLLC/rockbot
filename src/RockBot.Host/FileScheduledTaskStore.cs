using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// File-based scheduled task store. All tasks are persisted in a single JSON file at
/// <c>{agentBasePath}/scheduled-tasks.json</c>. The file is read on every call so
/// changes survive process restarts without requiring an explicit flush.
/// Thread safety via <see cref="SemaphoreSlim"/>.
/// </summary>
internal sealed class FileScheduledTaskStore : IScheduledTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly ILogger<FileScheduledTaskStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileScheduledTaskStore(
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileScheduledTaskStore> logger)
    {
        var basePath = profileOptions.Value.BasePath;
        if (!Path.IsPathRooted(basePath))
            basePath = Path.Combine(AppContext.BaseDirectory, basePath);

        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "scheduled-tasks.json");
        _logger = logger;

        logger.LogInformation("Scheduled task store: {Path}", _filePath);
    }

    /// <summary>Initialise with an explicit file path (for testing).</summary>
    internal FileScheduledTaskStore(string filePath, ILogger<FileScheduledTaskStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task SaveAsync(ScheduledTask task)
    {
        await _lock.WaitAsync();
        try
        {
            var tasks = await ReadAllAsync();
            tasks[task.Name] = task;
            await WriteAllAsync(tasks);
            _logger.LogDebug("Saved scheduled task '{Name}'", task.Name);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ScheduledTask?> GetAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            var tasks = await ReadAllAsync();
            return tasks.GetValueOrDefault(name);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<ScheduledTask>> ListAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var tasks = await ReadAllAsync();
            return tasks.Values
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string name)
    {
        await _lock.WaitAsync();
        try
        {
            var tasks = await ReadAllAsync();
            if (!tasks.Remove(name))
                return false;

            await WriteAllAsync(tasks);
            _logger.LogDebug("Deleted scheduled task '{Name}'", name);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateLastFiredAsync(string name, DateTimeOffset firedAt)
    {
        await _lock.WaitAsync();
        try
        {
            var tasks = await ReadAllAsync();
            if (!tasks.TryGetValue(name, out var existing))
                return;

            tasks[name] = existing with { LastFiredAt = firedAt };
            await WriteAllAsync(tasks);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Internal ─────────────────────────────────────────────────────────────

    private async Task<Dictionary<string, ScheduledTask>> ReadAllAsync()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, ScheduledTask>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            var list = JsonSerializer.Deserialize<List<ScheduledTask>>(json, JsonOptions) ?? [];
            return list.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Scheduled task store file is malformed; starting empty: {Path}", _filePath);
            return new Dictionary<string, ScheduledTask>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task WriteAllAsync(Dictionary<string, ScheduledTask> tasks)
    {
        var list = tasks.Values
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = JsonSerializer.Serialize(list, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
