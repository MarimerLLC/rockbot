using System.Collections.Concurrent;
using System.Text.Json;
using A2A;

namespace RockBot.A2A.Gateway;

/// <summary>
/// File-backed store for <see cref="TaskPushNotificationConfig"/> records.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/> with serialized file writes.
/// Configs are scoped by caller (tenant) and task ID.
/// </summary>
internal sealed class FilePushNotificationConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConcurrentDictionary<string, TaskPushNotificationConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _filePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool _loaded;

    public FilePushNotificationConfigStore(string? filePath)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
    }

    public async Task<TaskPushNotificationConfig> CreateAsync(
        string taskId, string configId, string tenant, PushNotificationConfig config, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);

        var entry = new TaskPushNotificationConfig
        {
            Id = configId,
            TaskId = taskId,
            Tenant = tenant,
            PushNotificationConfig = config
        };
        _configs[configId] = entry;
        await PersistAsync(ct);
        return entry;
    }

    public async Task<TaskPushNotificationConfig?> GetAsync(string configId, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return _configs.TryGetValue(configId, out var entry) ? entry : null;
    }

    public async Task<(List<TaskPushNotificationConfig> Configs, string NextPageToken)> ListAsync(
        string taskId, int? pageSize, string? pageToken, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);

        var matching = _configs.Values
            .Where(c => string.Equals(c.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Id)
            .ToList();

        if (!string.IsNullOrEmpty(pageToken))
        {
            var idx = matching.FindIndex(c => string.Equals(c.Id, pageToken, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                matching = matching.Skip(idx + 1).ToList();
        }

        var size = pageSize ?? 20;
        var page = matching.Take(size).ToList();
        var nextToken = page.Count == size && matching.Count > size ? page[^1].Id : string.Empty;

        return (page, nextToken);
    }

    public async Task<bool> DeleteAsync(string configId, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);

        if (_configs.TryRemove(configId, out _))
        {
            await PersistAsync(ct);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns all push notification configs for a given task, regardless of tenant.
    /// Used by <see cref="PushNotificationSender"/> to fire webhooks on status changes.
    /// </summary>
    public async Task<List<TaskPushNotificationConfig>> GetConfigsForTaskAsync(string taskId, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);

        return _configs.Values
            .Where(c => string.Equals(c.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            if (_loaded) return;

            if (_filePath is not null && File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(_filePath, ct);
                var entries = JsonSerializer.Deserialize<List<TaskPushNotificationConfig>>(json, JsonOptions);
                if (entries is not null)
                {
                    foreach (var entry in entries)
                    {
                        if (entry.Id is not null)
                            _configs.TryAdd(entry.Id, entry);
                    }
                }
            }

            _loaded = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (_filePath is null) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            var entries = _configs.Values.ToList();
            var json = JsonSerializer.Serialize(entries, JsonOptions);
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_filePath, json, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
