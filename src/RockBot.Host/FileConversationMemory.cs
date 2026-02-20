using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Conversation memory that persists sessions to JSON files so history survives agent restarts.
/// Wraps <see cref="InMemoryConversationMemory"/> for all in-memory access and asynchronously
/// flushes to <c>{BasePath}/{sessionId}.json</c> on every write. On startup, sessions whose last
/// turn falls within <see cref="ConversationMemoryOptions.SessionIdleTimeout"/> are restored from
/// disk so conversations resume seamlessly after a restart.
/// </summary>
internal sealed class FileConversationMemory : IConversationMemory, IHostedService, IDisposable
{
    private readonly InMemoryConversationMemory _inner;
    private readonly ConversationMemoryOptions _options;
    private readonly string _basePath;
    private readonly ILogger<FileConversationMemory> _logger;

    /// <summary>Per-session semaphores prevent concurrent writes racing on the same file.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FileConversationMemory(
        InMemoryConversationMemory inner,
        IOptions<ConversationMemoryOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileConversationMemory> logger)
    {
        _inner = inner;
        _options = options.Value;
        _basePath = ResolvePath(_options.BasePath, profileOptions.Value.BasePath);
        _logger = logger;

        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("Conversation persistence path: {Path}", _basePath);
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_basePath)) return;

        var cutoff = DateTimeOffset.UtcNow - _options.SessionIdleTimeout;
        var sessionsRestored = 0;
        var turnsRestored = 0;

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json"))
        {
            var sessionId = Path.GetFileNameWithoutExtension(file);
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var turns = JsonSerializer.Deserialize<List<ConversationTurn>>(json, JsonOptions);
                if (turns is null || turns.Count == 0) continue;

                // Skip sessions that have been idle longer than the configured timeout —
                // they would be discarded in memory anyway and may hold stale context.
                var lastActivity = turns.Max(t => t.Timestamp);
                if (lastActivity < cutoff)
                {
                    _logger.LogDebug(
                        "Skipping stale session {SessionId} (last activity: {LastActivity:u})",
                        sessionId, lastActivity);
                    continue;
                }

                foreach (var turn in turns)
                    await _inner.AddTurnAsync(sessionId, turn, cancellationToken);

                sessionsRestored++;
                turnsRestored += turns.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restore conversation session from {File}", file);
            }
        }

        if (sessionsRestored > 0)
            _logger.LogInformation(
                "Restored {Sessions} session(s) with {Turns} turn(s) from disk",
                sessionsRestored, turnsRestored);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── IConversationMemory ───────────────────────────────────────────────────

    public async Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        await _inner.AddTurnAsync(sessionId, turn, cancellationToken);
        await PersistSessionAsync(sessionId, cancellationToken);
    }

    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.GetTurnsAsync(sessionId, cancellationToken);

    public async Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _inner.ClearAsync(sessionId, cancellationToken);
        DeleteSessionFile(sessionId);
    }

    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_basePath))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var sessionIds = Directory.EnumerateFiles(_basePath, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => id is not null)
            .Cast<string>()
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(sessionIds);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task PersistSessionAsync(string sessionId, CancellationToken ct)
    {
        var sem = _writeLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var turns = await _inner.GetTurnsAsync(sessionId, ct);
            var path = Path.Combine(_basePath, $"{sessionId}.json");
            var json = JsonSerializer.Serialize(turns, JsonOptions);
            await File.WriteAllTextAsync(path, json, ct);
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
            _logger.LogWarning(ex,
                "Failed to delete conversation file for session {SessionId}", sessionId);
        }
        _writeLocks.TryRemove(sessionId, out var sem);
        sem?.Dispose();
    }

    /// <summary>
    /// Resolves the conversation storage path using the same convention as other file stores:
    /// absolute paths are used as-is; relative paths are combined under the profile base path.
    /// </summary>
    internal static string ResolvePath(string conversationPath, string profileBasePath)
    {
        if (Path.IsPathRooted(conversationPath))
            return conversationPath;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, conversationPath);
    }

    public void Dispose()
    {
        // _inner is a DI-managed singleton; its disposal is handled by the container.
        foreach (var (_, sem) in _writeLocks)
            sem.Dispose();
        _writeLocks.Clear();
    }
}
