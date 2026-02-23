using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// <see cref="ISharedMemory"/> that wraps <see cref="HybridCacheSharedMemory"/> and persists
/// entries to <c>{BasePath}/shared.json</c> so shared memory survives pod restarts.
/// TTL semantics are preserved: entries whose <c>ExpiresAt</c> has passed are discarded on load.
/// </summary>
internal sealed class FileSharedMemory : ISharedMemory, IHostedService
{
    private readonly HybridCacheSharedMemory _inner;
    private readonly string _basePath;
    private readonly ILogger<FileSharedMemory> _logger;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Timer? _sweepTimer;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record PersistedEntry(
        string Key,
        string Value,
        DateTimeOffset StoredAt,
        DateTimeOffset ExpiresAt,
        string? Category,
        IReadOnlyList<string>? Tags);

    public FileSharedMemory(
        HybridCacheSharedMemory inner,
        IOptions<SharedMemoryOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileSharedMemory> logger)
    {
        _inner = inner;
        _logger = logger;
        _basePath = FileWorkingMemory.ResolvePath(options.Value.BasePath, profileOptions.Value.BasePath);
        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("Shared memory persistence path: {Path}", _basePath);
    }

    private string FilePath => Path.Combine(_basePath, "shared.json");

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var path = FilePath;
        if (!File.Exists(path)) return;

        var now = DateTimeOffset.UtcNow;
        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json, JsonOptions);
            if (entries is null || entries.Count == 0) return;

            var live = entries.Where(e => e.ExpiresAt > now).ToList();
            if (live.Count == 0)
            {
                File.Delete(path);
                return;
            }

            foreach (var e in live)
            {
                var remainingTtl = e.ExpiresAt - now;
                await _inner.SetAsync(e.Key, e.Value, remainingTtl, e.Category, e.Tags);
            }

            _logger.LogInformation(
                "Restored {Entries} live shared memory entry(ies) from disk", live.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restore shared memory from {File}", path);
        }

        // Periodically sweep for fully-expired entries.
        _sweepTimer = new Timer(_ => _ = SweepExpiredAsync(), null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    private async Task SweepExpiredAsync()
    {
        var path = FilePath;
        if (!File.Exists(path)) return;

        var now = DateTimeOffset.UtcNow;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json, JsonOptions);
            if (entries is null || entries.All(e => e.ExpiresAt <= now))
            {
                File.Delete(path);
                _logger.LogInformation("Shared memory sweep removed expired file");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during shared memory sweep of {File}", path);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sweepTimer?.Dispose();
        _sweepTimer = null;
        return Task.CompletedTask;
    }

    // ── ISharedMemory ─────────────────────────────────────────────────────────

    public async Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        await _inner.SetAsync(key, value, ttl, category, tags);
        await PersistAsync();
    }

    public Task<string?> GetAsync(string key)
        => _inner.GetAsync(key);

    public Task<IReadOnlyList<SharedMemoryEntry>> ListAsync()
        => _inner.ListAsync();

    public async Task DeleteAsync(string key)
    {
        await _inner.DeleteAsync(key);
        await PersistAsync();
    }

    public async Task ClearAsync()
    {
        await _inner.ClearAsync();
        DeleteFile();
    }

    public Task<IReadOnlyList<SharedMemoryEntry>> SearchAsync(MemorySearchCriteria criteria)
        => _inner.SearchAsync(criteria);

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task PersistAsync()
    {
        await _writeLock.WaitAsync();
        try
        {
            var entries = await _inner.ListAsync();
            if (entries.Count == 0)
            {
                DeleteFile();
                return;
            }

            var persisted = entries
                .Select(e => new PersistedEntry(e.Key, e.Value, e.StoredAt, e.ExpiresAt, e.Category, e.Tags))
                .ToList();

            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            await File.WriteAllTextAsync(FilePath, json);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void DeleteFile()
    {
        try { File.Delete(FilePath); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete shared memory file");
        }
    }
}
