using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// <see cref="IWorkingMemory"/> that wraps <see cref="HybridCacheWorkingMemory"/> and persists
/// entries to disk so working memory survives pod restarts. Entries are grouped by their
/// top-level key segment into files like <c>session.json</c>, <c>patrol.json</c>,
/// <c>subagent.json</c>. Entries whose <c>ExpiresAt</c> has passed are discarded on load.
/// </summary>
internal sealed class FileWorkingMemory : IWorkingMemory, IHostedService
{
    private readonly HybridCacheWorkingMemory _inner;
    private readonly string _basePath;
    private readonly ILogger<FileWorkingMemory> _logger;

    /// <summary>Per-group semaphores prevent concurrent writes racing on the same file.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

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

    public FileWorkingMemory(
        HybridCacheWorkingMemory inner,
        IOptions<WorkingMemoryOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileWorkingMemory> logger)
    {
        _inner = inner;
        _logger = logger;
        _basePath = ResolvePath(options.Value.BasePath, profileOptions.Value.BasePath);
        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("Working memory persistence path: {Path}", _basePath);
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_basePath)) return;

        var now = DateTimeOffset.UtcNow;
        var filesRestored = 0;
        var entriesRestored = 0;

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json, JsonOptions);
                if (entries is null || entries.Count == 0) continue;

                var live = entries.Where(e => e.ExpiresAt > now).ToList();
                if (live.Count == 0)
                {
                    File.Delete(file);
                    continue;
                }

                foreach (var e in live)
                {
                    var remainingTtl = e.ExpiresAt - now;
                    await _inner.SetAsync(e.Key, e.Value, remainingTtl, e.Category, e.Tags);
                }

                filesRestored++;
                entriesRestored += live.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore working memory from {File}", file);
            }
        }

        if (filesRestored > 0)
            _logger.LogInformation(
                "Restored working memory from {Files} file(s) with {Entries} live entry(ies)",
                filesRestored, entriesRestored);

        // Periodically sweep for files whose entries have all expired.
        _sweepTimer = new Timer(_ => _ = SweepExpiredFilesAsync(), null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    private async Task SweepExpiredFilesAsync()
    {
        if (!Directory.Exists(_basePath)) return;

        var now = DateTimeOffset.UtcNow;
        var deleted = 0;

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(json, JsonOptions);
                if (entries is null || entries.All(e => e.ExpiresAt <= now))
                {
                    File.Delete(file);
                    var group = Path.GetFileNameWithoutExtension(file);
                    _writeLocks.TryRemove(group, out var sem);
                    sem?.Dispose();
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during working memory sweep of {File}", file);
            }
        }

        if (deleted > 0)
            _logger.LogInformation("Working memory sweep removed {Count} expired file(s)", deleted);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sweepTimer?.Dispose();
        _sweepTimer = null;
        return Task.CompletedTask;
    }

    // ── IWorkingMemory ────────────────────────────────────────────────────────

    public async Task SetAsync(string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        await _inner.SetAsync(key, value, ttl, category, tags);
        await PersistGroupAsync(GetGroup(key));
    }

    public Task<string?> GetAsync(string key)
        => _inner.GetAsync(key);

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string? prefix = null)
        => _inner.ListAsync(prefix);

    public async Task DeleteAsync(string key)
    {
        await _inner.DeleteAsync(key);
        await PersistGroupAsync(GetGroup(key));
    }

    public async Task ClearAsync(string? prefix = null)
    {
        await _inner.ClearAsync(prefix);

        if (string.IsNullOrEmpty(prefix))
        {
            // Clear everything — delete all group files
            foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
            {
                try { File.Delete(file); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete working memory file {File}", file);
                }
            }
            return;
        }

        // Re-persist the affected group (entries for that prefix are now gone)
        await PersistGroupAsync(GetGroup(prefix));
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(MemorySearchCriteria criteria, string? prefix = null)
        => _inner.SearchAsync(criteria, prefix);

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>Returns the top-level path segment used as the file name (e.g. "session", "patrol").</summary>
    private static string GetGroup(string key)
    {
        var slash = key.IndexOf('/');
        return slash > 0 ? key[..slash] : "_other";
    }

    private async Task PersistGroupAsync(string group)
    {
        var sem = _writeLocks.GetOrAdd(group, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            // Collect all in-memory entries belonging to this group
            var prefix = group == "_other" ? null : group + "/";
            var entries = await _inner.ListAsync(prefix);

            // If group is "_other", also include ungrouped keys (no slash)
            if (group == "_other")
                entries = entries.Where(e => !e.Key.Contains('/')).ToList();

            var path = Path.Combine(_basePath, $"{group}.json");

            if (entries.Count == 0)
            {
                try { File.Delete(path); }
                catch { /* ignore */ }
                return;
            }

            var persisted = entries
                .Select(e => new PersistedEntry(e.Key, e.Value, e.StoredAt, e.ExpiresAt, e.Category, e.Tags))
                .ToList();

            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
        finally
        {
            sem.Release();
        }
    }

    internal static string ResolvePath(string workingMemoryPath, string profileBasePath)
    {
        if (Path.IsPathRooted(workingMemoryPath))
            return workingMemoryPath;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, workingMemoryPath);
    }
}
