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
/// <para>
/// Turns evicted past <see cref="ConversationMemoryOptions.MaxTurnsPerSession"/> are appended to
/// <c>{ArchivePath}/{sessionId}.jsonl</c> (#509) so out-of-window recall still reaches them. The
/// archive belongs to the live session: a session that starts afresh — its first turn, or its first
/// turn after idle expiry, which also overwrites the session file — discards it, and so does
/// <see cref="ClearAsync"/>.
/// </para>
/// </summary>
internal sealed class FileConversationMemory : IConversationMemory, IHostedService, IDisposable
{
    private readonly InMemoryConversationMemory _inner;
    private readonly ConversationMemoryOptions _options;
    private readonly string _basePath;
    private readonly string _archivePath;
    private readonly ILogger<FileConversationMemory> _logger;
    private readonly IConversationLog? _conversationLog;

    /// <summary>Per-session semaphores prevent concurrent writes racing on the same file.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _writeLocks = new();

    /// <summary>Lines in each session's archive file, once known, so an append need not re-read it.</summary>
    private readonly ConcurrentDictionary<string, int> _archiveLines = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public FileConversationMemory(
        InMemoryConversationMemory inner,
        IOptions<ConversationMemoryOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileConversationMemory> logger,
        IConversationLog? conversationLog = null)
    {
        _inner = inner;
        _options = options.Value;
        _basePath = ResolvePath(_options.BasePath, profileOptions.Value.BasePath);
        _archivePath = ResolvePath(_options.ArchivePath, profileOptions.Value.BasePath);
        _logger = logger;
        _conversationLog = conversationLog;

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

        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories))
        {
            // Reconstruct session ID from the relative path so that slash-namespaced IDs
            // (e.g. "session/blazor-session") round-trip correctly through the file system.
            var relative = Path.GetRelativePath(_basePath, file);
            var sessionId = relative[..^".json".Length].Replace('\\', '/');
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

                // A file longer than MaxTurnsPerSession (the limit was lowered) evicts on restore;
                // archive those and rewrite the file so the next restart does not archive them again.
                var evicted = new List<ConversationTurn>();
                foreach (var turn in turns)
                    evicted.AddRange(_inner.AddTurn(sessionId, turn).Evicted);
                if (evicted.Count > 0)
                    await WithSessionLockAsync(sessionId, async () =>
                    {
                        await ArchiveAsync(sessionId, evicted, cancellationToken);
                        await WriteSessionFileAsync(sessionId, cancellationToken);
                    }, cancellationToken);

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
        // Under the session lock, so evictions reach the archive in the order they happened.
        await WithSessionLockAsync(sessionId, async () =>
        {
            var (started, evicted) = _inner.AddTurn(sessionId, turn);
            if (started)
                DeleteArchive(sessionId);
            if (evicted.Count > 0)
                await ArchiveAsync(sessionId, evicted, cancellationToken);
            await WriteSessionFileAsync(sessionId, cancellationToken);
        }, cancellationToken);
        if (_conversationLog is not null)
            _ = _conversationLog.AppendAsync(new ConversationLogEntry(sessionId, turn.Role, turn.Content, turn.Timestamp));
    }

    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
        => _inner.GetTurnsAsync(sessionId, cancellationToken);

    public async Task<IReadOnlyList<ConversationTurn>> GetArchivedTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // An expired session's archive belongs to a conversation that has ended. Its next turn
        // discards the archive; until then it must not be recalled as part of the new one.
        var max = _options.MaxArchivedTurnsPerSession;
        var path = ArchiveFile(sessionId);
        if (max <= 0 || !_inner.HasSession(sessionId) || !File.Exists(path))
            return [];

        List<ConversationTurn> turns = [];
        await WithSessionLockAsync(sessionId, async () =>
        {
            turns = await ReadArchiveAsync(path, cancellationToken);
            _archiveLines[sessionId] = turns.Count;
        }, cancellationToken);
        return turns.Count > max ? turns.GetRange(turns.Count - max, max) : turns;
    }

    public async Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _inner.ClearAsync(sessionId, cancellationToken);
        DeleteArchive(sessionId);
        DeleteSessionFile(sessionId);
    }

    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_basePath))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var sessionIds = Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_basePath, f))
            .Select(rel => rel[..^".json".Length].Replace('\\', '/'))
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(sessionIds);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task WithSessionLockAsync(string sessionId, Func<Task> action, CancellationToken ct)
    {
        var sem = _writeLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            await action();
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Writes the session's retained turns. Call under the session lock.</summary>
    private async Task WriteSessionFileAsync(string sessionId, CancellationToken ct)
    {
        var turns = await _inner.GetTurnsAsync(sessionId, ct);
        var path = Path.Combine(_basePath, $"{sessionId}.json");
        // Session IDs may contain '/' (e.g. "session/blazor-session") — ensure the
        // subdirectory exists before writing so we don't hit DirectoryNotFoundException.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(turns, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private string ArchiveFile(string sessionId) => Path.Combine(_archivePath, $"{sessionId}.jsonl");

    /// <summary>
    /// Appends evicted turns to the session's archive. Call under the session lock. The file is cut
    /// back to <see cref="ConversationMemoryOptions.MaxArchivedTurnsPerSession"/> once it runs a
    /// quarter over, so most appends do not rewrite it.
    /// </summary>
    private async Task ArchiveAsync(string sessionId, IReadOnlyList<ConversationTurn> evicted, CancellationToken ct)
    {
        var max = _options.MaxArchivedTurnsPerSession;
        if (max <= 0)
            return;
        try
        {
            var path = ArchiveFile(sessionId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = _archiveLines.TryGetValue(sessionId, out var known)
                ? known
                : File.Exists(path) ? (await ReadArchiveAsync(path, ct)).Count : 0;

            await File.AppendAllLinesAsync(path, evicted.Select(t => JsonSerializer.Serialize(t, JsonOptions)), ct);
            lines += evicted.Count;

            if (lines > max + Math.Max(1, max / 4))
            {
                var all = await ReadArchiveAsync(path, ct);
                var kept = all.Skip(Math.Max(0, all.Count - max)).ToList();
                await File.WriteAllLinesAsync(path, kept.Select(t => JsonSerializer.Serialize(t, JsonOptions)), ct);
                lines = kept.Count;
            }
            _archiveLines[sessionId] = lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Dropping an evicted turn is what happened before the archive existed; it must not fail the add.
            _archiveLines.TryRemove(sessionId, out _);
            _logger.LogWarning(ex, "Failed to archive {Count} evicted turn(s) for session {SessionId}",
                evicted.Count, sessionId);
        }
    }

    private async Task<List<ConversationTurn>> ReadArchiveAsync(string path, CancellationToken ct)
    {
        var turns = new List<ConversationTurn>();
        foreach (var line in await File.ReadAllLinesAsync(path, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<ConversationTurn>(line, JsonOptions) is { } turn)
                    turns.Add(turn);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Skipping an unreadable line in conversation archive {Path}", path);
            }
        }
        return turns;
    }

    private void DeleteArchive(string sessionId)
    {
        _archiveLines.TryRemove(sessionId, out _);
        var path = ArchiveFile(sessionId);
        if (!File.Exists(path))
            return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete conversation archive for session {SessionId}", sessionId);
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
