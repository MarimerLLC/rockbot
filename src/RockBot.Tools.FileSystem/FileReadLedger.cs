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
/// <b>Identical content (issue #677).</b> A per-session index from content hash to the paths
/// whose recorded version has that hash lets <see cref="FindFullyKnownCopy"/> answer "has this
/// session fully seen these exact bytes under any path?". In #677 a session read
/// <c>attachments/deck.md</c> in full, then was refused an overwrite of
/// <c>drafts/deck.md</c>, whose content was byte-for-byte the same. Nothing could have been
/// lost, and the refusal pushed the model into writing a renamed sibling copy instead.
/// </para>
/// <para>
/// <b>Refusals.</b> The ledger also remembers, per session, the paths it refused to overwrite
/// in the last <see cref="RefusalWindow"/>. That is diagnostic only: it lets <c>file_write</c>
/// log when a refusal is followed by a renamed copy (see <see cref="RecentRefusals"/>).
/// </para>
/// <para>
/// <b>Bounds.</b> The ledger is in-memory and process-local. It holds at most
/// <see cref="MaxEntries"/> entries and evicts the least recently touched quarter when it
/// overflows. The hash index mirrors the entries exactly (one membership per entry), so it
/// shares that bound. Refusals are capped per session and across sessions. Eviction or a
/// restart can only cause a refused overwrite. The refusal tells the model to read the
/// file, so the failure is safe.
/// </para>
/// </remarks>
internal sealed class FileReadLedger
{
    internal const int DefaultMaxEntries = 4096;

    /// <summary>How long a refused overwrite is remembered for sibling-copy diagnostics.</summary>
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromMinutes(10);

    private const int MaxRefusalsPerSession = 16;

    private readonly object _gate = new();
    private readonly Dictionary<(string Session, string Path), Entry> _entries = new();
    private readonly Dictionary<(string Session, string Hash), HashSet<string>> _byHash = new();
    private readonly Dictionary<string, List<(string Path, DateTimeOffset At)>> _refusals = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _clock;

    public FileReadLedger() : this(DefaultMaxEntries) { }

    internal FileReadLedger(int maxEntries, TimeProvider? timeProvider = null)
    {
        MaxEntries = Math.Max(1, maxEntries);
        _time = timeProvider ?? TimeProvider.System;
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
        {
            var key = Key(sessionId, fullPath);
            if (_entries.Remove(key, out var entry))
                Unindex(key, entry.Hash);
        }
    }

    /// <summary>
    /// A path other than <paramref name="excludePath"/> whose recorded version has hash
    /// <paramref name="versionHash"/> and which this session has seen in full, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The match is on the hash recorded when the session saw that path, not on that path's
    /// current content: what matters is that the session has seen these exact bytes. When
    /// several paths qualify, the most recently touched one is returned.
    /// </remarks>
    public string? FindFullyKnownCopy(string? sessionId, string versionHash, string? excludePath = null)
    {
        lock (_gate)
        {
            var session = sessionId ?? string.Empty;
            if (!_byHash.TryGetValue((session, versionHash), out var paths))
                return null;

            string? best = null;
            long bestTouched = long.MinValue;
            foreach (var path in paths)
            {
                if (excludePath is not null && string.Equals(path, excludePath, StringComparison.Ordinal))
                    continue;
                if (!_entries.TryGetValue((session, path), out var entry)
                    || !string.Equals(entry.Hash, versionHash, StringComparison.Ordinal)
                    || !IsFull(entry))
                {
                    continue;
                }

                if (entry.Touched > bestTouched)
                {
                    best = path;
                    bestTouched = entry.Touched;
                }
            }

            return best;
        }
    }

    /// <summary>Remembers that <c>file_write</c> refused to overwrite <paramref name="fullPath"/> for this session.</summary>
    public void RecordRefusal(string? sessionId, string fullPath)
    {
        lock (_gate)
        {
            var session = sessionId ?? string.Empty;
            var now = _time.GetUtcNow();

            if (!_refusals.TryGetValue(session, out var list))
            {
                list = [];
                _refusals[session] = list;
            }

            list.RemoveAll(r => now - r.At > RefusalWindow
                || string.Equals(r.Path, fullPath, StringComparison.Ordinal));
            list.Add((fullPath, now));
            if (list.Count > MaxRefusalsPerSession)
                list.RemoveRange(0, list.Count - MaxRefusalsPerSession);

            if (_refusals.Count > MaxEntries)
            {
                // Drop sessions whose refusals have all expired, then the oldest if still over.
                foreach (var stale in _refusals.Where(kv => kv.Value.All(r => now - r.At > RefusalWindow))
                             .Select(kv => kv.Key).ToList())
                {
                    _refusals.Remove(stale);
                }

                while (_refusals.Count > MaxEntries)
                {
                    var oldest = _refusals.MinBy(kv => kv.Value.Count == 0 ? DateTimeOffset.MinValue : kv.Value[^1].At).Key;
                    _refusals.Remove(oldest);
                }
            }
        }
    }

    /// <summary>
    /// Full paths this session was refused an overwrite of within the last
    /// <see cref="RefusalWindow"/>, most recent first.
    /// </summary>
    public IReadOnlyList<string> RecentRefusals(string? sessionId)
    {
        lock (_gate)
        {
            if (!_refusals.TryGetValue(sessionId ?? string.Empty, out var list))
                return [];

            // The list is in the order refusals were recorded.
            var now = _time.GetUtcNow();
            return list.Where(r => now - r.At <= RefusalWindow)
                .Select(r => r.Path)
                .Reverse()
                .ToList();
        }
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
            return new FileCoverage(
                IsFull(entry) ? FileCoverageStatus.Full : FileCoverageStatus.Partial, entry.TotalLines, ranges);
        }
    }

    private static bool IsFull(Entry entry) =>
        entry.TotalLines == 0
        || (entry.Ranges.Count == 1 && entry.Ranges[0].Start <= 1 && entry.Ranges[0].End >= entry.TotalLines);

    private Entry GetOrReset(string? sessionId, string fullPath, string versionHash, int totalLines)
    {
        var key = Key(sessionId, fullPath);
        if (!_entries.TryGetValue(key, out var entry)
            || !string.Equals(entry.Hash, versionHash, StringComparison.Ordinal))
        {
            if (entry is not null)
                Unindex(key, entry.Hash);

            // Stamp before evicting, or the new entry would look like the oldest one.
            entry = new Entry(versionHash, totalLines) { Touched = ++_clock };
            _entries[key] = entry;
            Index(key, versionHash);
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
        foreach (var (key, entry) in _entries.OrderBy(e => e.Value.Touched).Take(drop).ToList())
        {
            _entries.Remove(key);
            Unindex(key, entry.Hash);
        }
    }

    private void Index((string Session, string Path) key, string hash)
    {
        if (!_byHash.TryGetValue((key.Session, hash), out var paths))
        {
            paths = new HashSet<string>(StringComparer.Ordinal);
            _byHash[(key.Session, hash)] = paths;
        }
        paths.Add(key.Path);
    }

    private void Unindex((string Session, string Path) key, string hash)
    {
        if (_byHash.TryGetValue((key.Session, hash), out var paths)
            && paths.Remove(key.Path)
            && paths.Count == 0)
        {
            _byHash.Remove((key.Session, hash));
        }
    }

    /// <summary>Total paths held in the hash index. For tests: it must always equal <see cref="Count"/>.</summary>
    internal int HashIndexPathCount
    {
        get { lock (_gate) return _byHash.Values.Sum(p => p.Count); }
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
