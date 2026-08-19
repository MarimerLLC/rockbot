using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// JSONL-based implementation of <see cref="IConversationLog"/>.
/// All turns are appended to a single file: <c>{BasePath}/turns.jsonl</c>.
/// Thread-safe via a single <see cref="SemaphoreSlim"/>.
/// </summary>
internal sealed class FileConversationLog : IConversationLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly ILogger<FileConversationLog> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public FileConversationLog(
        IOptions<ConversationLogOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileConversationLog> logger)
    {
        var basePath = ResolvePath(options.Value.BasePath, profileOptions.Value.BasePath);
        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "turns.jsonl");
        _logger = logger;

        _logger.LogInformation("Conversation log path: {Path}", _filePath);
    }

    public async Task AppendAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, cancellationToken);
            _logger.LogDebug("ConversationLog: appended [{Role}] for session {SessionId}", entry.Role, entry.SessionId);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationLogEntry>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return Array.Empty<ConversationLogEntry>();

            var entries = new List<ConversationLogEntry>();
            var lines = await File.ReadAllLinesAsync(_filePath, cancellationToken);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<ConversationLogEntry>(line, JsonOptions);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "ConversationLog: failed to deserialize entry from {Path}", _filePath);
                }
            }
            return entries;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Streaming, bounded read of a single session. Keeps at most <paramref name="maxEntries"/>
    /// entries in memory via a ring buffer, so the log file's total size does not affect the
    /// working set — this runs on a user-facing latency path, unlike the dream passes that use
    /// <see cref="ReadAllAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<ConversationLogEntry>> ReadSessionAsync(
        string sessionId, int maxEntries, CancellationToken cancellationToken = default)
    {
        if (maxEntries <= 0) return [];

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return Array.Empty<ConversationLogEntry>();

            // Ring buffer: appending past capacity drops the oldest, so the newest
            // maxEntries survive regardless of how long the file is.
            var window = new Queue<ConversationLogEntry>(maxEntries);

            await foreach (var line in File.ReadLinesAsync(_filePath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Every line is parsed rather than pre-filtered on a raw substring match:
                // the serializer escapes non-ASCII by default, so a session id containing
                // escaped characters would not appear verbatim in its own log lines.
                ConversationLogEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<ConversationLogEntry>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "ConversationLog: failed to deserialize entry from {Path}", _filePath);
                    continue;
                }

                if (entry is null) continue;
                if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)) continue;

                if (window.Count == maxEntries) window.Dequeue();
                window.Enqueue(entry);
            }

            // Entries are appended in chronological order, but a restart or a clock
            // adjustment can leave them slightly out of order — sort so callers can
            // rely on chronological indexing.
            return window.OrderBy(e => e.Timestamp).ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Streaming scan producing one summary per session. Holds only the per-session
    /// aggregates, never the entries themselves.
    /// </summary>
    public async Task<IReadOnlyList<ConversationLogSessionInfo>> ListLoggedSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath))
                return Array.Empty<ConversationLogSessionInfo>();

            var aggregates = new Dictionary<string, (int Count, DateTimeOffset First, DateTimeOffset Last)>(
                StringComparer.Ordinal);

            await foreach (var line in File.ReadLinesAsync(_filePath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                ConversationLogEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<ConversationLogEntry>(line, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "ConversationLog: failed to deserialize entry from {Path}", _filePath);
                    continue;
                }

                if (entry is null) continue;

                if (aggregates.TryGetValue(entry.SessionId, out var agg))
                {
                    aggregates[entry.SessionId] = (
                        agg.Count + 1,
                        entry.Timestamp < agg.First ? entry.Timestamp : agg.First,
                        entry.Timestamp > agg.Last ? entry.Timestamp : agg.Last);
                }
                else
                {
                    aggregates[entry.SessionId] = (1, entry.Timestamp, entry.Timestamp);
                }
            }

            return aggregates
                .Select(kvp => new ConversationLogSessionInfo(
                    kvp.Key, kvp.Value.Count, kvp.Value.First, kvp.Value.Last))
                .OrderByDescending(s => s.LastTimestamp)
                .ToList();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_filePath))
            {
                // Move-with-overwrite to a single rolling .bak file so the
                // previous dream cycle's conversations stay recoverable until
                // the next clear. Move is atomic (same directory) and removes
                // the original file in one step.
                var bakPath = _filePath + ".bak";
                File.Move(_filePath, bakPath, overwrite: true);
                _logger.LogDebug("ConversationLog: cleared (previous content moved to {BakPath})", bakPath);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static string ResolvePath(string path, string basePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(basePath)
            ? basePath
            : Path.Combine(AppContext.BaseDirectory, basePath);

        return Path.Combine(baseDir, path);
    }
}
