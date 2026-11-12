using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Wisp;

/// <summary>
/// File-based wisp execution log. All records are appended to a single JSONL file:
/// <c>{basePath}/wisp-executions.jsonl</c>. One JSON object per line.
/// </summary>
/// <remarks>
/// Queries are served from an in-memory copy of the file that is kept current by
/// tail-reading only the bytes appended since the previous query, so a query costs a
/// file stat plus the parse of any new lines rather than a full read and parse of the
/// whole file. The cache is discarded and rebuilt once when the file shrinks or is
/// replaced (retention trim, deletion, external rewrite).
/// </remarks>
internal sealed class FileWispExecutionLog : IWispExecutionLog, IPrunableLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Bytes kept from the start of the file to detect it being replaced.</summary>
    private const int HeadFingerprintLength = 256;

    /// <summary>
    /// Upper bound on the bytes kept from the last consumed line. The whole line is
    /// normally kept (records are a few KB) so its unique <c>wispId</c> is part of the
    /// comparison; trailing bytes alone are near-identical across records.
    /// </summary>
    private const int MaxTailFingerprintLength = 64 * 1024;

    private readonly string _filePath;
    private readonly ILogger<FileWispExecutionLog> _logger;

    // Guards the file writer, retention trim and the cache below.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Cache of the on-disk records (guarded by _lock) ──────────────────────
    private readonly List<WispExecutionRecord> _records = [];
    // Hashes are lowercase hex, so case-insensitive keys change nothing in practice; they
    // keep the indexed lookups consistent with the OrdinalIgnoreCase comparisons callers use.
    private readonly Dictionary<string, List<WispExecutionRecord>> _byHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<WispExecutionRecord>> _byShape = new(StringComparer.OrdinalIgnoreCase);
    private long _offset;
    private byte[] _headFingerprint = [];
    private byte[] _tailFingerprint = [];
    private long _linesParsed;

    public FileWispExecutionLog(WispOptions options, ILogger<FileWispExecutionLog> logger)
    {
        var basePath = ResolvePath(options);
        Directory.CreateDirectory(basePath);
        _filePath = Path.Combine(basePath, "wisp-executions.jsonl");
        _logger = logger;
    }

    /// <summary>
    /// The directory the execution log lives in. Shared with the schema-migration descriptor so
    /// the marker lands beside the log rather than in a second guess at the same location.
    /// </summary>
    internal static string ResolvePath(WispOptions options) =>
        options.SharedVolumePath ?? Path.Combine(AppContext.BaseDirectory, "wisp-log");

    /// <summary>
    /// Total number of non-blank lines parsed from disk since construction. Test hook for
    /// asserting that per-query work tracks new lines, not file size.
    /// </summary>
    internal long LinesParsed => Interlocked.Read(ref _linesParsed);

    public async Task AppendAsync(WispExecutionRecord record, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var line = JsonSerializer.Serialize(record, JsonOptions);
            await File.AppendAllTextAsync(_filePath, line + Environment.NewLine, ct);
            _logger.LogDebug("Wisp execution logged [{WispId}] success={Success}", record.WispId, record.Succeeded);
            // The cache picks this line up on the next query's tail read.
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Single append-only file shared across all sessions. Retention trims it to the
    /// last <see cref="LogRetentionPolicy.MaxLinesPerFile"/> lines, serialized against
    /// the append writer so a trim never races an execution record.
    /// </summary>
    public async Task<int> PruneAsync(LogRetentionPolicy policy, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var removed = await JsonlLogRetention.TrimToLastLinesAsync(_filePath, policy.MaxLinesPerFile, _logger, ct);
            if (removed > 0)
                ResetCache();
            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<WispExecutionRecord>> QueryRecentAsync(
        DateTimeOffset since, int maxResults, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await RefreshAsync(ct);
            return _records
                .Where(r => r.Timestamp >= since)
                .OrderBy(r => r.Timestamp)
                .Take(maxResults)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<WispExecutionRecord>> QueryByHashAsync(
        string hash, DateTimeOffset since, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(hash))
            return [];

        await _lock.WaitAsync(ct);
        try
        {
            await RefreshAsync(ct);

            // A record lands in both indexes when its shape and definition hashes are
            // equal (or when a caller passes a value that matches both), so de-duplicate.
            var matches = new HashSet<WispExecutionRecord>(ReferenceEqualityComparer.Instance);
            if (_byShape.TryGetValue(hash, out var sameShape))
                matches.UnionWith(sameShape);
            if (_byHash.TryGetValue(hash, out var sameHash))
                matches.UnionWith(sameHash);

            return matches
                .Where(r => r.Timestamp >= since)
                .OrderBy(r => r.Timestamp)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WispExecutionRecord?> FindRecentFailureAsync(
        string definitionHash, string? sessionId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await RefreshAsync(ct);
            if (definitionHash is null || !_byHash.TryGetValue(definitionHash, out var sameHash))
                return null;

            // Find the most recent failed record with the same definition hash,
            // preferring records from the same session
            return sameHash
                .Where(r => !r.Succeeded)
                .OrderByDescending(r => r.SessionId == sessionId ? 1 : 0)
                .ThenByDescending(r => r.Timestamp)
                .FirstOrDefault();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetCanonicalBodyAsync(string definitionHash, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(definitionHash))
            return null;

        await _lock.WaitAsync(ct);
        try
        {
            await RefreshAsync(ct);
            if (!_byHash.TryGetValue(definitionHash, out var sameHash))
                return null;

            return sameHash
                .Where(r => r.Succeeded && !string.IsNullOrEmpty(r.DefinitionBody))
                .OrderBy(r => r.Timestamp)
                .Select(r => r.DefinitionBody)
                .FirstOrDefault();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Brings the cache in line with the file: parses only complete lines appended since
    /// the last refresh, or reloads from the start when the file shrank or was replaced.
    /// Caller must hold <see cref="_lock"/>. Best-effort: I/O failures are logged and the
    /// existing cache is kept.
    /// </summary>
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            ResetCache();
            return;
        }

        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024, useAsync: true);

            var length = stream.Length;
            if (length < _offset || !await ConsumedRegionUnchangedAsync(stream, ct))
                ResetCache();

            if (length == _offset)
                return;

            stream.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[length - _offset];
            await stream.ReadExactlyAsync(buffer, ct);

            var lineStart = 0;
            var lastLineStart = 0;
            while (true)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', lineStart);
                if (newline < 0)
                    break; // trailing partial line: a writer is mid-append; pick it up next time

                ParseLine(buffer.AsSpan(lineStart, newline - lineStart));
                lastLineStart = lineStart;
                lineStart = newline + 1;
            }

            // Parsing is synchronous, so the cache and offset advance together.
            if (lineStart > 0)
            {
                if (_offset == 0)
                    _headFingerprint = buffer[..Math.Min(HeadFingerprintLength, lineStart)];
                _tailFingerprint = buffer[lastLineStart..lineStart];
                if (_tailFingerprint.Length > MaxTailFingerprintLength)
                    _tailFingerprint = _tailFingerprint[^MaxTailFingerprintLength..];
                _offset += lineStart;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to read wisp execution log file {Path}", _filePath);
        }
    }

    private void ParseLine(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r')
            line = line[..^1];

        var text = Encoding.UTF8.GetString(line);
        if (string.IsNullOrWhiteSpace(text))
            return;

        Interlocked.Increment(ref _linesParsed);
        try
        {
            var record = JsonSerializer.Deserialize<WispExecutionRecord>(text, JsonOptions);
            if (record is not null)
                AddToCache(record);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize wisp execution record");
        }
    }

    private void AddToCache(WispExecutionRecord record)
    {
        _records.Add(record);
        AddToIndex(_byHash, record.DefinitionHash, record);
        AddToIndex(_byShape, record.ShapeHash, record);
    }

    private static void AddToIndex(
        Dictionary<string, List<WispExecutionRecord>> index, string? key, WispExecutionRecord record)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index[key] = list;
        }
        list.Add(record);
    }

    private void ResetCache()
    {
        _records.Clear();
        _byHash.Clear();
        _byShape.Clear();
        _offset = 0;
        _headFingerprint = [];
        _tailFingerprint = [];
    }

    /// <summary>
    /// Whether the bytes already parsed into the cache still look the same on disk: both
    /// the start of the file and the last consumed line (ending at <see cref="_offset"/>)
    /// must match.
    /// Catches a file replaced via temp+move (retention trim by another instance, manual
    /// edit) even when the new file is the same length or longer, including one that keeps
    /// the first record but rewrites later ones. This is a cheap probe, not a full
    /// checksum: an edit confined to the middle of the consumed region is not detected,
    /// which is acceptable for an append-only log whose only rewrite is a tail-keeping trim.
    /// </summary>
    private async Task<bool> ConsumedRegionUnchangedAsync(FileStream stream, CancellationToken ct)
    {
        if (_offset == 0)
            return true;

        var head = await ReadRangeAsync(stream, 0, _headFingerprint.Length, ct);
        if (!head.AsSpan().SequenceEqual(_headFingerprint))
            return false;

        var tail = await ReadRangeAsync(stream, _offset - _tailFingerprint.Length, _tailFingerprint.Length, ct);
        return tail.AsSpan().SequenceEqual(_tailFingerprint);
    }

    private static async Task<byte[]> ReadRangeAsync(FileStream stream, long start, int count, CancellationToken ct)
    {
        var bytes = new byte[count];
        stream.Seek(start, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }
}
