using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// In-process failure cluster store with PVC-backed persistence. Hot reads/writes
/// land in a <see cref="ConcurrentDictionary{TKey,TValue}"/>; durability comes from
/// an append-only JSONL log of record events plus a periodic JSON snapshot of the
/// full state. On startup the snapshot is loaded then the JSONL is replayed; on
/// snapshot completion the JSONL is truncated to bound disk growth.
///
/// See <c>design/self-repair.md</c> Phase 5.
/// </summary>
internal sealed class FileFailureClusterStore : IFailureClusterStore, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private const string SnapshotFileName = "failure-clusters.snapshot.json";
    private const string JsonlFileName = "failure-clusters.jsonl";

    private readonly FailureClusterOptions _options;
    private readonly ILogger<FileFailureClusterStore> _logger;
    private readonly string _basePath;
    private readonly string _snapshotPath;
    private readonly string _jsonlPath;

    private readonly ConcurrentDictionary<ClusterKey, FailureCluster> _clusters = new();

    /// <summary>Serialises all file I/O — snapshot writes, JSONL appends, and JSONL truncation.</summary>
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private Timer? _flushTimer;
    private bool _loaded;

    public FileFailureClusterStore(
        IOptions<FailureClusterOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<FileFailureClusterStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _basePath = ResolvePath(_options.BasePath, profileOptions.Value.BasePath);
        _snapshotPath = Path.Combine(_basePath, SnapshotFileName);
        _jsonlPath = Path.Combine(_basePath, JsonlFileName);
        Directory.CreateDirectory(_basePath);
        _logger.LogInformation("Failure cluster store path: {Path}", _basePath);
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        if (_options.FlushInterval > TimeSpan.Zero)
        {
            _flushTimer = new Timer(
                _ => _ = FlushAsync(CancellationToken.None),
                state: null,
                dueTime: _options.FlushInterval,
                period: _options.FlushInterval);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_flushTimer is not null)
        {
            await _flushTimer.DisposeAsync();
            _flushTimer = null;
        }

        await FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_flushTimer is not null)
        {
            await _flushTimer.DisposeAsync();
            _flushTimer = null;
        }
        _fileLock.Dispose();
    }

    // ── IFailureClusterStore ──────────────────────────────────────────────────

    public async Task RecordAsync(
        ClusterKey key,
        string? sessionId,
        string errorMessage,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var truncated = Truncate(errorMessage ?? string.Empty, _options.MaxSampleMessageLength);

        // The in-memory mutation and the JSONL append run under the same file lock
        // as FlushAsync. This guarantees that any record observable in the snapshot
        // (in-memory state at the time of capture) has either already been written
        // to the JSONL (and is about to be truncated) or has no JSONL entry at all.
        // The startup replay still skips JSONL events older than the snapshot, so
        // records that landed in the snapshot are never double-applied.
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            _clusters.AddOrUpdate(
                key,
                _ => CreateCluster(key, sessionId, truncated, at),
                (_, existing) => MergeCluster(existing, sessionId, truncated, at));

            var line = SerializeJsonlEvent(key, sessionId, truncated, at);
            await File.AppendAllTextAsync(_jsonlPath, line, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public Task<IReadOnlyList<FailureCluster>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<FailureCluster> snapshot = _clusters.Values
            .OrderByDescending(c => c.LastSeen)
            .ToList();
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<FailureCluster>> GetEscalatableAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var window = _options.EscalationWindow;
        var minCount = _options.EscalationCountThreshold;
        var minSessions = _options.EscalationSessionThreshold;

        IReadOnlyList<FailureCluster> escalatable = _clusters.Values
            .Where(c => c.Count >= minCount
                        && c.SessionIds.Count >= minSessions
                        && (now - c.LastSeen) < window)
            .OrderByDescending(c => c.LastSeen)
            .ToList();
        return Task.FromResult(escalatable);
    }

    // ── Cluster mutation helpers ──────────────────────────────────────────────

    private FailureCluster CreateCluster(
        ClusterKey key, string? sessionId, string errorMessage, DateTimeOffset at)
    {
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(sessionId))
            sessions.Add(sessionId);

        var samples = new List<string> { errorMessage };

        return new FailureCluster(
            Key: key,
            Count: 1,
            SessionIds: sessions,
            FirstSeen: at,
            LastSeen: at,
            SampleErrorMessages: samples);
    }

    private FailureCluster MergeCluster(
        FailureCluster existing, string? sessionId, string errorMessage, DateTimeOffset at)
    {
        var sessions = new HashSet<string>(existing.SessionIds, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(sessionId)
            && sessions.Count < _options.MaxSessionIdsPerCluster)
        {
            sessions.Add(sessionId);
        }

        var samples = new List<string>(existing.SampleErrorMessages) { errorMessage };
        if (samples.Count > _options.MaxSampleMessages)
        {
            samples.RemoveRange(0, samples.Count - _options.MaxSampleMessages);
        }

        return existing with
        {
            Count = existing.Count + 1,
            SessionIds = sessions,
            FirstSeen = existing.FirstSeen <= at ? existing.FirstSeen : at,
            LastSeen = existing.LastSeen >= at ? existing.LastSeen : at,
            SampleErrorMessages = samples,
        };
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (_loaded) return;

            DateTimeOffset snapshotWrittenAt = DateTimeOffset.MinValue;
            var loaded = 0;

            if (File.Exists(_snapshotPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_snapshotPath, cancellationToken);
                    var dto = JsonSerializer.Deserialize<SnapshotDto>(json, JsonOptions);
                    if (dto is not null)
                    {
                        snapshotWrittenAt = dto.WrittenAt;
                        foreach (var c in dto.Clusters ?? [])
                        {
                            var cluster = ClusterFromDto(c);
                            if (cluster is not null)
                            {
                                _clusters[cluster.Key] = cluster;
                                loaded++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load failure cluster snapshot from {Path}", _snapshotPath);
                }
            }

            var replayed = 0;
            if (File.Exists(_jsonlPath))
            {
                using var stream = new FileStream(
                    _jsonlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    JsonlEventDto? evt = null;
                    try
                    {
                        evt = JsonSerializer.Deserialize<JsonlEventDto>(line, JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex,
                            "Skipping malformed failure-cluster JSONL line in {Path}", _jsonlPath);
                        continue;
                    }

                    if (evt is null || evt.Key is null) continue;
                    if (evt.At < snapshotWrittenAt) continue; // already in snapshot

                    ClusterKey key;
                    try
                    {
                        key = new ClusterKey(evt.Key.Server, evt.Key.Tool, evt.Key.ErrorClass);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }

                    _clusters.AddOrUpdate(
                        key,
                        _ => CreateCluster(key, evt.SessionId, evt.ErrorMessage ?? string.Empty, evt.At),
                        (_, existing) => MergeCluster(existing, evt.SessionId, evt.ErrorMessage ?? string.Empty, evt.At));
                    replayed++;
                }
            }

            _loaded = true;

            if (loaded > 0 || replayed > 0)
            {
                _logger.LogInformation(
                    "Loaded {Loaded} cluster(s) from snapshot and replayed {Replayed} JSONL event(s)",
                    loaded, replayed);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static string SerializeJsonlEvent(
        ClusterKey key, string? sessionId, string errorMessage, DateTimeOffset at)
    {
        var evt = new JsonlEventDto
        {
            At = at,
            Key = new ClusterKeyDto { Server = key.Server, Tool = key.Tool, ErrorClass = key.ErrorClass },
            SessionId = sessionId,
            ErrorMessage = errorMessage,
        };

        return JsonSerializer.Serialize(evt, JsonOptions) + Environment.NewLine;
    }

    /// <summary>
    /// Writes the in-memory cluster state to the snapshot atomically and
    /// truncates the JSONL log. Safe to call concurrently — serialised by
    /// <see cref="_fileLock"/>.
    /// </summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = new SnapshotDto
            {
                WrittenAt = DateTimeOffset.UtcNow,
                Clusters = _clusters.Values
                    .OrderByDescending(c => c.LastSeen)
                    .Select(ClusterToDto)
                    .ToList(),
            };

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            var tempPath = _snapshotPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, _snapshotPath, overwrite: true);

            // Truncate JSONL — events up to snapshot.WrittenAt are now durable in the snapshot.
            if (File.Exists(_jsonlPath))
            {
                using var fs = new FileStream(
                    _jsonlPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush failure cluster snapshot");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // ── DTOs and conversions ──────────────────────────────────────────────────

    private sealed class SnapshotDto
    {
        public DateTimeOffset WrittenAt { get; set; }
        public List<ClusterDto>? Clusters { get; set; }
    }

    private sealed class ClusterDto
    {
        public ClusterKeyDto? Key { get; set; }
        public int Count { get; set; }
        public List<string>? SessionIds { get; set; }
        public DateTimeOffset FirstSeen { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public List<string>? SampleErrorMessages { get; set; }
    }

    private sealed class ClusterKeyDto
    {
        public string Server { get; set; } = string.Empty;
        public string Tool { get; set; } = string.Empty;
        public string ErrorClass { get; set; } = string.Empty;
    }

    private sealed class JsonlEventDto
    {
        public DateTimeOffset At { get; set; }
        public ClusterKeyDto? Key { get; set; }
        public string? SessionId { get; set; }
        public string? ErrorMessage { get; set; }
    }

    private static ClusterDto ClusterToDto(FailureCluster c) => new()
    {
        Key = new ClusterKeyDto { Server = c.Key.Server, Tool = c.Key.Tool, ErrorClass = c.Key.ErrorClass },
        Count = c.Count,
        SessionIds = c.SessionIds.ToList(),
        FirstSeen = c.FirstSeen,
        LastSeen = c.LastSeen,
        SampleErrorMessages = c.SampleErrorMessages.ToList(),
    };

    private static FailureCluster? ClusterFromDto(ClusterDto dto)
    {
        if (dto.Key is null) return null;

        ClusterKey key;
        try
        {
            key = new ClusterKey(dto.Key.Server, dto.Key.Tool, dto.Key.ErrorClass);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var sessions = new HashSet<string>(dto.SessionIds ?? [], StringComparer.Ordinal);
        var samples = dto.SampleErrorMessages ?? [];

        return new FailureCluster(
            Key: key,
            Count: dto.Count,
            SessionIds: sessions,
            FirstSeen: dto.FirstSeen,
            LastSeen: dto.LastSeen,
            SampleErrorMessages: samples);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Truncate(string s, int max)
    {
        if (max <= 0 || s.Length <= max) return s;
        return s[..max] + "…";
    }

    private static string ResolvePath(string path, string profileBasePath)
    {
        if (Path.IsPathRooted(path))
            return path;

        var baseDir = Path.IsPathRooted(profileBasePath)
            ? profileBasePath
            : Path.Combine(AppContext.BaseDirectory, profileBasePath);

        return Path.Combine(baseDir, path);
    }
}
