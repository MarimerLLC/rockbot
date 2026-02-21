using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// <see cref="IWorkingMemory"/> that wraps <see cref="HybridCacheWorkingMemory"/> and persists
/// sessions to <c>{BasePath}/{sessionId}.json</c> so working memory survives pod restarts.
/// TTL semantics are preserved: entries whose <c>ExpiresAt</c> has passed are discarded on load
/// and never surfaced after they expire in memory.
/// </summary>
internal sealed class FileWorkingMemory : IWorkingMemory, IHostedService
{
    private readonly HybridCacheWorkingMemory _inner;
    private readonly string _basePath;
    private readonly ILogger<FileWorkingMemory> _logger;

    /// <summary>Per-session semaphores prevent concurrent writes racing on the same file.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Serialization DTO — captures all fields needed to reconstruct an entry after restart.
    /// </summary>
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
        var sessionsRestored = 0;
        var entriesRestored = 0;

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
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
                    await _inner.SetAsync(sessionId, e.Key, e.Value, remainingTtl, e.Category, e.Tags);
                }

                sessionsRestored++;
                entriesRestored += live.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore working memory session from {File}", file);
            }
        }

        if (sessionsRestored > 0)
            _logger.LogInformation(
                "Restored {Sessions} working memory session(s) with {Entries} live entry(ies) from disk",
                sessionsRestored, entriesRestored);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── IWorkingMemory ────────────────────────────────────────────────────────

    public async Task SetAsync(string sessionId, string key, string value, TimeSpan? ttl = null,
        string? category = null, IReadOnlyList<string>? tags = null)
    {
        await _inner.SetAsync(sessionId, key, value, ttl, category, tags);
        await PersistSessionAsync(sessionId);
    }

    public Task<string?> GetAsync(string sessionId, string key)
        => _inner.GetAsync(sessionId, key);

    public Task<IReadOnlyList<WorkingMemoryEntry>> ListAsync(string sessionId)
        => _inner.ListAsync(sessionId);

    public async Task DeleteAsync(string sessionId, string key)
    {
        await _inner.DeleteAsync(sessionId, key);
        await PersistSessionAsync(sessionId);
    }

    public async Task ClearAsync(string sessionId)
    {
        await _inner.ClearAsync(sessionId);
        DeleteSessionFile(sessionId);
    }

    public Task<IReadOnlyList<WorkingMemoryEntry>> SearchAsync(string sessionId, MemorySearchCriteria criteria)
        => _inner.SearchAsync(sessionId, criteria);

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task PersistSessionAsync(string sessionId)
    {
        var sem = _writeLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        try
        {
            var entries = await _inner.ListAsync(sessionId);
            if (entries.Count == 0)
            {
                DeleteSessionFile(sessionId);
                return;
            }

            var persisted = entries
                .Select(e => new PersistedEntry(e.Key, e.Value, e.StoredAt, e.ExpiresAt, e.Category, e.Tags))
                .ToList();

            var path = Path.Combine(_basePath, $"{sessionId}.json");
            var json = JsonSerializer.Serialize(persisted, JsonOptions);
            await File.WriteAllTextAsync(path, json);
        }
        finally
        {
            sem.Release();
        }
    }

    private void DeleteSessionFile(string sessionId)
    {
        var path = Path.Combine(_basePath, $"{sessionId}.json");
        try { File.Delete(path); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete working memory file for session {SessionId}", sessionId);
        }
        _writeLocks.TryRemove(sessionId, out var sem);
        sem?.Dispose();
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
