using System.Security.Cryptography;
using System.Text;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// How much of a file's current version a session has seen.
/// </summary>
internal enum FileCoverageStatus
{
    /// <summary>The session has no record of reading this file.</summary>
    NeverRead,

    /// <summary>The session read the file, but it has changed since.</summary>
    Stale,

    /// <summary>The session has seen some, but not all, lines of the current version.</summary>
    Partial,

    /// <summary>The session has seen every line of the current version.</summary>
    Full,
}

/// <summary>
/// Result of <see cref="FileReadLedger.Check"/>.
/// </summary>
/// <param name="Status">Coverage classification.</param>
/// <param name="TotalLines">Line count of the version the ranges refer to.</param>
/// <param name="Ranges">Merged, sorted 1-based inclusive line ranges the session has seen.</param>
internal readonly record struct FileCoverage(
    FileCoverageStatus Status,
    int TotalLines,
    IReadOnlyList<(int Start, int End)> Ranges)
{
    /// <summary>The first line of the current version the session has not seen, or 0 if none.</summary>
    public int FirstUnseenLine
    {
        get
        {
            var next = 1;
            foreach (var (start, end) in Ranges)
            {
                if (start > next) return next;
                next = Math.Max(next, end + 1);
            }
            return next <= TotalLines ? next : 0;
        }
    }

    /// <summary>The seen ranges in words, e.g. "lines 1–120 and 300–410".</summary>
    public string DescribeSeen()
    {
        if (Ranges.Count == 0)
            return "no lines";

        var parts = Ranges.Select(r => r.Start == r.End ? $"{r.Start}" : $"{r.Start}–{r.End}").ToList();
        var noun = Ranges.Count == 1 && Ranges[0].Start == Ranges[0].End ? "line" : "lines";
        var joined = parts.Count switch
        {
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => string.Join(", ", parts[..^1]) + ", and " + parts[^1],
        };
        return $"{noun} {joined}";
    }
}

/// <summary>
/// Per-session record of which file versions each session has seen in full. Backs the
/// read-before-overwrite rule on <c>file_write</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> In issue #664 a model read an 11.5 KB deck through a capped result, saw
/// only its head and tail, and called <c>file_write</c> with a reconstruction of those
/// parts. That destroyed the deck. A tool that replaces a whole file must not trust a
/// caller that has not seen the whole file. This is the same rule Claude Code applies.
/// </para>
/// <para>
/// <b>Keys.</b> Entries are keyed by session id and resolved full path. A subagent has its
/// own session id, so a subagent must read what the primary wrote before overwriting it,
/// and the other way round. Each entry stores a content hash of the version it describes.
/// If anything else changes the file (another session, a script pod, a person), the hash no
/// longer matches and the entry counts as stale.
/// </para>
/// <para>
/// <b>Coverage.</b> A version counts as seen when a single unpaged read returned all of it,
/// when paged reads of that same version together covered every line, or when this session
/// itself wrote it with <c>file_write</c>.
/// </para>
/// <para>
/// <b>Bounds.</b> The ledger is in-memory and process-local. It holds at most
/// <see cref="MaxEntries"/> entries and evicts the least recently touched quarter when it
/// overflows. Eviction or a restart can only cause a refused overwrite. The refusal tells
/// the model to read the file, so the failure is safe.
/// </para>
/// </remarks>
internal sealed class FileReadLedger
{
    internal const int DefaultMaxEntries = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<(string Session, string Path), Entry> _entries = new();
    private long _clock;

    public FileReadLedger() : this(DefaultMaxEntries) { }

    internal FileReadLedger(int maxEntries)
    {
        MaxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>Largest number of (session, path) entries kept before eviction.</summary>
    public int MaxEntries { get; }

    /// <summary>Current number of entries. For tests and diagnostics.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Hex SHA-256 of <paramref name="bytes"/>, the version identity entries carry.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Records that <paramref name="sessionId"/> saw lines
    /// <paramref name="firstLine"/>–<paramref name="lastLine"/> of the version identified by
    /// <paramref name="versionHash"/>. A read of a different version discards earlier coverage.
    /// </summary>
    public void RecordRead(
        string? sessionId, string fullPath, string versionHash, int totalLines, int firstLine, int lastLine)
    {
        lock (_gate)
        {
            var entry = GetOrReset(sessionId, fullPath, versionHash, totalLines);
            if (totalLines > 0 && lastLine >= firstLine)
                entry.Add(Math.Max(1, firstLine), Math.Min(totalLines, lastLine));
        }
    }

    /// <summary>
    /// Records that <paramref name="sessionId"/> knows the whole version identified by
    /// <paramref name="versionHash"/>, because it read all of it or wrote it.
    /// </summary>
    public void RecordFullyKnown(string? sessionId, string fullPath, string versionHash, int totalLines)
    {
        lock (_gate)
        {
            var entry = GetOrReset(sessionId, fullPath, versionHash, totalLines);
            if (totalLines > 0)
                entry.Add(1, totalLines);
        }
    }

    /// <summary>Drops whatever the ledger knows about this session and path.</summary>
    public void Forget(string? sessionId, string fullPath)
    {
        lock (_gate)
            _entries.Remove(Key(sessionId, fullPath));
    }

    /// <summary>
    /// How much of the version identified by <paramref name="versionHash"/> this session has seen.
    /// </summary>
    public FileCoverage Check(string? sessionId, string fullPath, string versionHash)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(Key(sessionId, fullPath), out var entry))
                return new FileCoverage(FileCoverageStatus.NeverRead, 0, []);

            entry.Touched = ++_clock;

            if (!string.Equals(entry.Hash, versionHash, StringComparison.Ordinal))
                return new FileCoverage(FileCoverageStatus.Stale, entry.TotalLines, entry.Ranges.ToArray());

            var ranges = entry.Ranges.ToArray();
            var full = entry.TotalLines == 0
                || (ranges.Length == 1 && ranges[0].Start <= 1 && ranges[0].End >= entry.TotalLines);
            return new FileCoverage(
                full ? FileCoverageStatus.Full : FileCoverageStatus.Partial, entry.TotalLines, ranges);
        }
    }

    private Entry GetOrReset(string? sessionId, string fullPath, string versionHash, int totalLines)
    {
        var key = Key(sessionId, fullPath);
        if (!_entries.TryGetValue(key, out var entry)
            || !string.Equals(entry.Hash, versionHash, StringComparison.Ordinal))
        {
            // Stamp before evicting, or the new entry would look like the oldest one.
            entry = new Entry(versionHash, totalLines) { Touched = ++_clock };
            _entries[key] = entry;
            EvictIfNeeded();
            return entry;
        }

        entry.Touched = ++_clock;
        return entry;
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count <= MaxEntries)
            return;

        var drop = Math.Max(1, _entries.Count / 4);
        foreach (var key in _entries.OrderBy(e => e.Value.Touched).Take(drop).Select(e => e.Key).ToList())
            _entries.Remove(key);
    }

    private static (string, string) Key(string? sessionId, string fullPath) => (sessionId ?? string.Empty, fullPath);

    private sealed class Entry(string hash, int totalLines)
    {
        public string Hash { get; } = hash;
        public int TotalLines { get; } = totalLines;
        public List<(int Start, int End)> Ranges { get; } = [];
        public long Touched { get; set; }

        /// <summary>Adds a range, keeping <see cref="Ranges"/> sorted and merged.</summary>
        public void Add(int start, int end)
        {
            Ranges.Add((start, end));
            Ranges.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<(int Start, int End)>(Ranges.Count);
            foreach (var r in Ranges)
            {
                if (merged.Count > 0 && r.Start <= merged[^1].End + 1)
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
                else
                    merged.Add(r);
            }

            Ranges.Clear();
            Ranges.AddRange(merged);
        }
    }
}

/// <summary>
/// Line arithmetic shared by <c>file_read</c> paging and the overwrite guard, so both count
/// lines the same way.
/// </summary>
internal sealed class LineIndex
{
    private readonly string _content;
    private readonly List<int> _starts;

    public LineIndex(string content)
    {
        _content = content;
        _starts = [];
        if (content.Length == 0)
            return;

        _starts.Add(0);
        for (var i = 0; i < content.Length; i++)
        {
            // A trailing newline ends the last line; it does not start an empty one.
            if (content[i] == '\n' && i + 1 < content.Length)
                _starts.Add(i + 1);
        }
    }

    /// <summary>Number of lines. An empty file has none; a trailing newline adds none.</summary>
    public int Count => _starts.Count;

    /// <summary>Offset of 1-based line <paramref name="line"/>.</summary>
    public int StartOf(int line) => _starts[line - 1];

    /// <summary>Offset just past 1-based line <paramref name="line"/>, including its terminator.</summary>
    public int EndOf(int line) => line < _starts.Count ? _starts[line] : _content.Length;

    /// <summary>Length of 1-based line <paramref name="line"/>, including its terminator.</summary>
    public int LengthOf(int line) => EndOf(line) - StartOf(line);

    /// <summary>Text of lines <paramref name="first"/>–<paramref name="last"/>, terminators included.</summary>
    public string Slice(int first, int last) => _content[StartOf(first)..EndOf(last)];

    /// <summary>Counts lines in <paramref name="content"/> without keeping an index.</summary>
    public static int CountLines(string content)
    {
        if (content.Length == 0)
            return 0;

        var count = 1;
        for (var i = 0; i < content.Length - 1; i++)
        {
            if (content[i] == '\n')
                count++;
        }
        return count;
    }
}

/// <summary>
/// Decodes file bytes the same way <see cref="File.ReadAllTextAsync(string, CancellationToken)"/>
/// does: honours a byte-order mark and otherwise assumes UTF-8, replacing invalid bytes.
/// </summary>
internal static class LenientText
{
    public static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
