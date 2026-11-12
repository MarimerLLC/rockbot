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

    /// <summary>Bytes from the start of the file kept to detect the file being replaced.</summary>
    private const int FingerprintLength = 256;

    private readonly string _filePath;
    private readonly ILogger<FileWispExecutionLog> _logger;

    // Guards the file writer, retention trim and the cache below.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Cache of the on-disk records (guarded by _lock) ──────────────────────
    private readonly List<WispExecutionRecord> _records = [];
    private readonly Dictionary<string, List<WispExecutionRecord>> _byHash = new(StringComparer.Ordinal);
    private long _offset;
    private byte[] _headFingerprint = [];
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
            if (length < _offset || (_offset > 0 && !await HeadMatchesAsync(stream, ct)))
                ResetCache();

            if (length == _offset)
                return;

            if (_offset == 0)
                _headFingerprint = await ReadHeadAsync(stream, ct);

            stream.Seek(_offset, SeekOrigin.Begin);
            var buffer = new byte[length - _offset];
            await stream.ReadExactlyAsync(buffer, ct);

            var lineStart = 0;
            while (true)
            {
                var newline = Array.IndexOf(buffer, (byte)'\n', lineStart);
                if (newline < 0)
                    break; // trailing partial line: a writer is mid-append; pick it up next time

                ParseLine(buffer.AsSpan(lineStart, newline - lineStart));
                lineStart = newline + 1;
            }

            // Parsing is synchronous, so the cache and offset advance together.
            _offset += lineStart;
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
        if (record.DefinitionHash is null)
            return;
        if (!_byHash.TryGetValue(record.DefinitionHash, out var list))
        {
            list = [];
            _byHash[record.DefinitionHash] = list;
        }
        list.Add(record);
    }

    private void ResetCache()
    {
        _records.Clear();
        _byHash.Clear();
        _offset = 0;
        _headFingerprint = [];
    }

    private static async Task<byte[]> ReadHeadAsync(FileStream stream, CancellationToken ct)
    {
        var head = new byte[(int)Math.Min(FingerprintLength, stream.Length)];
        stream.Seek(0, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(head, ct);
        return head;
    }

    private async Task<bool> HeadMatchesAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length < _headFingerprint.Length)
            return false;
        var head = new byte[_headFingerprint.Length];
        stream.Seek(0, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(head, ct);
        return head.AsSpan().SequenceEqual(_headFingerprint);
    }
}
