using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RockBot.Agent.McpBridge;

/// <summary>An entry a <see cref="JsonLedger{TEntry}"/> can keep.</summary>
internal interface ILedgerEntry<out TSelf>
{
    /// <summary>The entry's id, unique within its ledger.</summary>
    string Key { get; }

    /// <summary>Settled: nothing more will happen to it but the purge.</summary>
    bool IsSettled { get; }

    /// <summary>When it settled, which starts its retention.</summary>
    DateTimeOffset? ResolvedAt { get; }

    /// <summary>A deep copy, so callers never hold the ledger's own instances.</summary>
    TSelf Clone();
}

/// <summary>
/// A small durable file of entries the bridge writes on its own: the pending-question ledger and
/// the background-task ledger. Every change is written before the call returns (callers write
/// ahead of telling anyone), atomically, and serialized.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Crash-safe.</b> Each write goes to a temporary file that is renamed over the old
///   one, so a crash mid-write leaves the previous file intact.</item>
///   <item><b>Single writer.</b> Only the bridge writes, and its writes are serialized.</item>
///   <item><b>Bounded.</b> Settled entries are purged <see cref="RetainSettled"/> after they settle.</item>
///   <item><b>Versioned.</b> The file carries a top-level <c>version</c>. Adding an optional
///   property is absorbed by the tolerant reader; anything else bumps the version and ships a
///   migration (<c>design/schema-migrations.md</c>).</item>
///   <item><b>Unreadable is not fatal.</b> A file that can't be read is copied aside as
///   <c>.corrupt</c> and the ledger starts empty rather than refuse to run.</item>
/// </list>
/// </remarks>
internal sealed class JsonLedger<TEntry> where TEntry : class, ILedgerEntry<TEntry>
{
    /// <summary>How long a settled entry is kept before it is purged.</summary>
    public static readonly TimeSpan RetainSettled = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly string _what;
    private readonly int _version;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<TEntry>? _entries;

    /// <param name="path">Where the file lives.</param>
    /// <param name="what">What it is, for log messages ("MCP pending-question ledger").</param>
    /// <param name="version">The version this build writes.</param>
    public JsonLedger(string path, string what, int version, ILogger logger, TimeProvider? time = null)
    {
        _path = path;
        _what = what;
        _version = version;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Where the ledger lives.</summary>
    public string Path => _path;

    /// <summary>The clock settled entries are aged by.</summary>
    public TimeProvider Time => _time;

    /// <summary>A copy of every entry.</summary>
    public async Task<IReadOnlyList<TEntry>> ReadAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return [.. Load().Select(e => e.Clone())];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A copy of one entry, or null.</summary>
    public async Task<TEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return Load().FirstOrDefault(e => e.Key == key)?.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds an entry and writes the ledger before returning.</summary>
    public Task AddAsync(TEntry entry, CancellationToken ct = default) =>
        MutateAsync(entries => entries.Add(entry.Clone()), ct);

    /// <summary>
    /// Applies <paramref name="update"/> to the entry for <paramref name="key"/>, if there is one,
    /// and writes the ledger. Returns whether the entry existed.
    /// </summary>
    public async Task<bool> UpdateAsync(string key, Action<TEntry> update, CancellationToken ct = default)
    {
        var found = false;
        await MutateAsync(entries =>
        {
            if (entries.FirstOrDefault(e => e.Key == key) is { } entry)
            {
                update(entry);
                found = true;
            }
        }, ct);
        return found;
    }

    /// <summary>Applies <paramref name="update"/> to every entry in one write (startup reconciliation).</summary>
    public Task UpdateAllAsync(Action<TEntry> update, CancellationToken ct = default) =>
        MutateAsync(entries => entries.ForEach(update), ct);

    private async Task MutateAsync(Action<List<TEntry>> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Change a copy and keep it only once it is on disk, so a failed write leaves memory
            // and file agreeing on the previous ledger.
            var entries = Load().Select(e => e.Clone()).ToList();
            mutate(entries);
            Purge(entries);
            await WriteAsync(entries);
            _entries = entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Purge(List<TEntry> entries)
    {
        var cutoff = _time.GetUtcNow() - RetainSettled;
        entries.RemoveAll(e => e.IsSettled && e.ResolvedAt is { } at && at < cutoff);
    }

    /// <summary>Reads the file once; later calls use the cached copy, which only this class changes.</summary>
    private List<TEntry> Load()
    {
        if (_entries is not null)
            return _entries;

        _entries = [];
        if (!File.Exists(_path))
            return _entries;

        try
        {
            var file = JsonSerializer.Deserialize<LedgerFile>(File.ReadAllText(_path), JsonOptions);
            if (file is null)
                return _entries;

            if (file.Version > _version)
            {
                _logger.LogWarning(
                    "{What} {Path} is version {Version}, newer than this build's {Current}; reading it as is",
                    _what, _path, file.Version, _version);
            }

            _entries = [.. file.Entries.Where(e => e is not null && !string.IsNullOrEmpty(e.Key))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep a copy for a person to look at and start empty rather than refuse to run.
            _logger.LogError(ex, "{What} {Path} could not be read; starting with an empty ledger", _what, _path);
            TryPreserveCorrupt();
        }

        return _entries;
    }

    private void TryPreserveCorrupt()
    {
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private async Task WriteAsync(List<TEntry> entries)
    {
        var json = JsonSerializer.Serialize(new LedgerFile { Version = _version, Entries = entries }, JsonOptions);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        else
            Directory.CreateDirectory(directory);

        var temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Not cancellable: a write abandoned halfway would leave the cache ahead of the file.
            await File.WriteAllTextAsync(temp, json, CancellationToken.None);
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* the write already failed */ }
            throw;
        }
    }

    private sealed class LedgerFile
    {
        public int Version { get; set; }
        public List<TEntry> Entries { get; set; } = [];
    }
}
