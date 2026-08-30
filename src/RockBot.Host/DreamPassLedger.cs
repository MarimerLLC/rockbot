using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Records, per dream pass, the fingerprint of the input that pass last actually ran an LLM
/// call on and when it did so. A pass consults the ledger before calling the model: when its
/// inputs hash to the same value as last time, the call is skipped.
/// </summary>
/// <remarks>
/// <para>
/// This exists because several dream passes are corpus-wide rather than delta-driven — skill
/// consolidation ships the whole skill catalog, graph consolidation the whole graph, the
/// contradiction sweep the whole claim/feedback corpus. Without a gate they re-ask the model
/// the same question, on the same bytes, on every cycle forever, and the prompt grows with the
/// corpus rather than with the user's activity.
/// </para>
/// <para>
/// The gate is deliberately not absolute. <see cref="DreamOptions.DreamPassMaxSkipInterval"/>
/// forces a run once a pass has been skipped for that long, because some directives are
/// time-dependent in ways the input hash cannot see — graph consolidation prunes entities by
/// staleness, so an unchanged graph still becomes prunable through the mere passage of time.
/// The floor bounds the cost (one run per interval instead of one per cycle) without silently
/// switching those behaviours off.
/// </para>
/// <para>
/// Persisted next to the agent profile so the gate survives a pod restart; a lost or corrupt
/// ledger degrades to "run everything once", never to "skip something forever".
/// </para>
/// </remarks>
internal sealed class DreamPassLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>The last input a pass ran on, and when that run happened.</summary>
    internal sealed record PassRecord(string Fingerprint, DateTimeOffset LastRunAt);

    private readonly Dictionary<string, PassRecord> _records;
    private readonly string _path;
    private readonly ILogger _logger;
    private bool _dirty;

    private DreamPassLedger(string path, Dictionary<string, PassRecord> records, ILogger logger)
    {
        _path = path;
        _records = records;
        _logger = logger;
    }

    /// <summary>Default filename, relative to <see cref="AgentProfileOptions.BasePath"/>.</summary>
    internal const string FileName = "dream-pass-ledger.json";

    /// <summary>
    /// Reads the ledger from disk. A missing, unreadable, or malformed file yields an empty
    /// ledger rather than an exception: the cost of forgetting is one extra pass, and the dream
    /// cycle must not fail over bookkeeping.
    /// </summary>
    public static async Task<DreamPassLedger> LoadAsync(string path, ILogger logger)
    {
        var records = new Dictionary<string, PassRecord>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, PassRecord>>(json, JsonOptions);
                if (loaded is not null)
                    foreach (var (pass, record) in loaded)
                        if (!string.IsNullOrWhiteSpace(record?.Fingerprint))
                            records[pass] = record;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "DreamPassLedger: failed to read {Path}; every gated pass will run this cycle", path);
            }
        }

        return new DreamPassLedger(path, records, logger);
    }

    /// <summary>The record for <paramref name="passName"/>, or <c>null</c> if it has never run.</summary>
    public PassRecord? Get(string passName) =>
        _records.TryGetValue(passName, out var record) ? record : null;

    /// <summary>
    /// Whether <paramref name="passName"/> may skip its LLM call this cycle.
    /// </summary>
    public bool ShouldSkip(
        string passName,
        string fingerprint,
        DateTimeOffset now,
        TimeSpan maxSkipInterval) =>
        ShouldSkip(Get(passName), fingerprint, now, maxSkipInterval);

    /// <summary>
    /// Pure form of <see cref="ShouldSkip(string, string, DateTimeOffset, TimeSpan)"/>, separated
    /// so the decision can be unit-tested without touching the filesystem.
    /// </summary>
    /// <remarks>
    /// A non-positive <paramref name="maxSkipInterval"/> disables the floor, making the gate
    /// absolute — an unchanged input is then skipped indefinitely.
    /// </remarks>
    internal static bool ShouldSkip(
        PassRecord? record,
        string fingerprint,
        DateTimeOffset now,
        TimeSpan maxSkipInterval)
    {
        // Never run: nothing to compare against.
        if (record is null) return false;

        // Inputs moved. This is the common "the agent did something" case.
        if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            return false;

        if (maxSkipInterval <= TimeSpan.Zero) return true;

        // Floor reached — run anyway so time-dependent directives still fire. A clock that has
        // gone backwards (restore, timezone change) reads as "not yet due" rather than
        // triggering a run; the next forward tick corrects it.
        return now - record.LastRunAt < maxSkipInterval;
    }

    /// <summary>Records that <paramref name="passName"/> ran to completion on this input.</summary>
    public void Record(string passName, string fingerprint, DateTimeOffset now)
    {
        _records[passName] = new PassRecord(fingerprint, now);
        _dirty = true;
    }

    /// <summary>
    /// Writes the ledger back to disk if anything changed. Uses <see cref="AtomicFile"/> so a
    /// crash mid-write cannot leave a truncated ledger behind — which would read back as "every
    /// pass is due" rather than as corruption.
    /// </summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        if (!_dirty) return;

        try
        {
            await AtomicFile.WriteAllTextAsync(
                _path, JsonSerializer.Serialize(_records, JsonOptions), ct).ConfigureAwait(false);
            _dirty = false;
        }
        catch (Exception ex)
        {
            // A ledger that fails to persist costs one redundant pass next cycle. Never fatal.
            _logger.LogWarning(ex, "DreamPassLedger: failed to write {Path}", _path);
        }
    }
}
