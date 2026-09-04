using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// One entry as the previous audit run saw it. Deliberately not the whole
/// <see cref="MemoryEntry"/>: the state file is written every run and must not become a second
/// copy of the corpus.
/// </summary>
/// <param name="Id">Entry id.</param>
/// <param name="Archived">Whether it was in the archive tier.</param>
/// <param name="ArchivedAt">When it was archived, for purge-eligibility arithmetic.</param>
/// <param name="ReinforcementCount">Reinforcement count, so real re-observation can be told from merge arithmetic.</param>
/// <param name="MergedFromCount">How many sources its provenance named.</param>
/// <param name="Category">Category path, or null.</param>
internal sealed record MemoryAuditEntryRow(
    string Id,
    bool Archived,
    DateTimeOffset? ArchivedAt,
    int ReinforcementCount,
    int MergedFromCount,
    string? Category);

/// <summary>
/// Private carry-over between audit runs: what the store looked like last time, plus the
/// bookkeeping the deltas need.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from <c>snapshots.jsonl</c> on purpose. The trend file is a public, appendable,
/// human-and-sidecar-readable record whose rows never change; this is a rewritten working file
/// whose shape is free to move. Losing it costs one run's deltas, never the trend.
/// </para>
/// <para>
/// Recording every id is what makes a hard delete detectable at all. Counting entries would say
/// the corpus shrank; recording ids says <em>which</em> facts vanished, which is the difference
/// between a number and a finding.
/// </para>
/// </remarks>
internal sealed record MemoryAuditState
{
    /// <summary>When the run that wrote this state happened.</summary>
    public DateTimeOffset TakenAt { get; init; }

    /// <summary>Id of the snapshot this state accompanies.</summary>
    public string SnapshotId { get; init; } = string.Empty;

    /// <summary>Every entry on disk at that moment.</summary>
    public IReadOnlyList<MemoryAuditEntryRow> Entries { get; init; } = [];

    /// <summary>
    /// Rejected merge cluster hash → how many consecutive runs it has been rejected on. A
    /// cluster that stops being rejected drops out rather than decaying, so the count always
    /// means "consecutive".
    /// </summary>
    public IReadOnlyDictionary<string, int> RejectedClusterRuns { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Entry ids stamped as rejected merge sources at the last run.</summary>
    public IReadOnlyList<string> RejectedSourceIds { get; init; } = [];

    /// <summary>
    /// Host start times, trimmed to the last 60 days. Nothing else in the agent records
    /// restarts, and a restart storm is the documented cause of a consolidation storm.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> ProcessStarts { get; init; } = [];

    /// <summary>When the sample eval last ran.</summary>
    public DateTimeOffset? LastEvalAt { get; init; }

    /// <summary>Corpus fingerprint the eval last ran against, so an unchanged store skips it.</summary>
    public string? LastEvalFingerprint { get; init; }

    /// <summary>When the full-report digest was last pushed.</summary>
    public DateTimeOffset? LastDigestAt { get; init; }

    /// <summary>How long process-start timestamps are kept.</summary>
    internal static readonly TimeSpan ProcessStartRetention = TimeSpan.FromDays(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Reads the state at <paramref name="path"/>. A missing, unreadable or malformed file
    /// yields <c>null</c> — the audit then reports a first run with no deltas, which is honest,
    /// rather than failing or inventing comparisons.
    /// </summary>
    internal static async Task<MemoryAuditState?> LoadAsync(
        string path, ILogger logger, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<MemoryAuditState>(json, JsonOptions);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Memory audit: could not read {Path}; this run reports as a first run", path);
            return null;
        }
    }

    /// <summary>Writes the state atomically, so an interrupted run cannot truncate it.</summary>
    internal async Task SaveAsync(string path, CancellationToken ct = default) =>
        await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(this, JsonOptions), ct)
            .ConfigureAwait(false);
}
