using System.Globalization;
namespace RockBot.Host;

/// <summary>
/// The properties the memory store is supposed to hold, checked against what is actually on
/// disk.
/// </summary>
/// <remarks>
/// <para>
/// Split into two families. <em>Structural</em> invariants describe the corpus itself and are
/// true or false regardless of configuration — a merge whose replacement does not exist is
/// broken on any deployment. <em>Threshold</em> invariants compare a measurement against a
/// tuning knob and say "this is more than you said you would accept".
/// </para>
/// <para>
/// Only three findings reach <c>alert</c>: data destroyed outside the purge, a large drop in
/// live entries, and provenance pointing at something that no longer exists. Those are the
/// three shapes every real incident so far has taken. Everything else is a warning, because a
/// channel that alerts on tuning drift stops being read before the day it matters.
/// </para>
/// </remarks>
internal static class MemoryAuditInvariants
{
    internal const string MergedFromResolves = "merged-from-resolves";
    internal const string ArchiveFieldsPresent = "archive-fields-present";
    internal const string LiveNotMergeSource = "live-not-merge-source";
    internal const string MergeChainUnbroken = "merge-chain-unbroken";
    internal const string NoHardDeleteOutsidePurge = "no-hard-delete-outside-purge";
    internal const string NoRepeatedRejection = "no-repeated-rejection";
    internal const string NetGrowthThreshold = "net-growth-threshold";
    internal const string ChainDepthThreshold = "chain-depth-threshold";
    internal const string RejectedMergesThreshold = "rejected-merges-threshold";
    internal const string LossPercentThreshold = "loss-percent-threshold";
    internal const string NoMalformedFiles = "no-malformed-files";

    /// <summary>Findings serious enough that something was actually lost.</summary>
    private static readonly HashSet<string> AlertInvariants =
    [
        NoHardDeleteOutsidePurge,
        LossPercentThreshold,
        MergeChainUnbroken
    ];

    /// <summary>
    /// Runs every invariant against the walked corpus and the snapshot's already-computed
    /// counters.
    /// </summary>
    /// <param name="entries">Every entry on disk, live and archived.</param>
    /// <param name="snapshot">The snapshot so far — invariants and status are not yet set.</param>
    /// <param name="dreamOptions">Supplies the archive retention window.</param>
    /// <param name="auditOptions">Thresholds.</param>
    /// <param name="now">Agent-local time of this run.</param>
    /// <param name="elapsedDays">
    /// Days since the previous run, or zero when that window was too short to measure a rate
    /// over. Zero disables the rate-based invariants rather than dividing by it.
    /// </param>
    /// <param name="previousLive">Live count at the previous run, or null on a first run.</param>
    internal static IReadOnlyList<MemoryAuditInvariantViolation> Check(
        IReadOnlyList<MemoryEntry> entries,
        MemoryAuditSnapshot snapshot,
        DreamOptions dreamOptions,
        MemoryAuditOptions auditOptions,
        DateTimeOffset now,
        double elapsedDays,
        int? previousLive)
    {
        var violations = new List<MemoryAuditInvariantViolation>();

        var byId = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            byId[entry.Id] = entry;

        // ── Structural ────────────────────────────────────────────────────────

        // merged-from-resolves: a merge names sources that are not on disk. Dangling provenance
        // is by design once the sources have been purged, so only merges too recent for that to
        // have happened are counted.
        var provenanceHorizon = dreamOptions.MemoryArchiveRetention > TimeSpan.Zero
            ? now - dreamOptions.MemoryArchiveRetention
            : DateTimeOffset.MinValue;

        var danglingProvenance = entries
            .Where(e => MergedAt(e) is { } mergedAt && mergedAt > provenanceHorizon)
            .Where(e => MemoryAuditAnalyzer.MergedFromIds(e).Any(source => !byId.ContainsKey(source)))
            .Select(e => e.Id)
            .ToList();

        if (danglingProvenance.Count > 0)
            violations.Add(new MemoryAuditInvariantViolation(
                MergedFromResolves,
                $"{danglingProvenance.Count} recent merge(s) name source entries that are no longer on disk, " +
                "and are too recent for the retention purge to explain it.",
                Cap(danglingProvenance)));

        // archive-fields-present: the two archive fields must move together, or an archived
        // entry has no recorded justification and nobody can tell why it left recall.
        var brokenArchiveFields = entries
            .Where(e => (e.ArchivedAt is not null) != !string.IsNullOrWhiteSpace(e.ArchiveReason))
            .Select(e => e.Id)
            .ToList();

        if (brokenArchiveFields.Count > 0)
            violations.Add(new MemoryAuditInvariantViolation(
                ArchiveFieldsPresent,
                $"{brokenArchiveFields.Count} entry(s) have an archive timestamp without a reason, or vice versa.",
                Cap(brokenArchiveFields)));

        // live-not-merge-source: an entry that has been merged away should not still be in
        // recall. Both copies of the fact would then surface, and the next cycle would merge
        // the merge.
        var mergeSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            foreach (var source in MemoryAuditAnalyzer.MergedFromIds(entry))
                mergeSources.Add(source);

        var liveSources = entries
            .Where(e => e.ArchivedAt is null && mergeSources.Contains(e.Id))
            .Select(e => e.Id)
            .ToList();

        if (liveSources.Count > 0)
            violations.Add(new MemoryAuditInvariantViolation(
                LiveNotMergeSource,
                $"{liveSources.Count} entry(s) are named as the source of a merge but are still live in recall.",
                Cap(liveSources)));

        // merge-chain-unbroken: the shape issue #506 found. Sources archived "merged into X"
        // where X exists nowhere means the merge's replacement was never written, or was later
        // destroyed — the fact is gone and only the tombstone remains.
        var brokenChains = entries
            .Where(e => MergedIntoTarget(e) is { } target && !byId.ContainsKey(target))
            .Select(e => e.Id)
            .ToList();

        if (brokenChains.Count > 0)
            violations.Add(new MemoryAuditInvariantViolation(
                MergeChainUnbroken,
                $"{brokenChains.Count} archived entry(s) were merged into a replacement that does not exist. " +
                "Their content has no other copy.",
                Cap(brokenChains)));

        if (snapshot.MalformedFiles > 0)
            violations.Add(new MemoryAuditInvariantViolation(
                NoMalformedFiles,
                $"{snapshot.MalformedFiles} file(s) under the memory root would not deserialize and were skipped.",
                []));

        // ── Loss ──────────────────────────────────────────────────────────────

        if (snapshot.HardDeletedOutsidePurge > auditOptions.MaxHardDeletesOutsidePurge)
            violations.Add(new MemoryAuditInvariantViolation(
                NoHardDeleteOutsidePurge,
                $"{snapshot.HardDeletedOutsidePurge} entry(s) disappeared from disk that the retention purge " +
                $"cannot account for (limit {auditOptions.MaxHardDeletesOutsidePurge}).",
                []));

        if (previousLive is > 0)
        {
            var lossPercent = (previousLive.Value - snapshot.Live) * 100.0 / previousLive.Value;
            if (lossPercent > auditOptions.MaxLossPercentBetweenSnapshots)
                violations.Add(new MemoryAuditInvariantViolation(
                    LossPercentThreshold,
                    $"Live entries fell {Num(lossPercent, 1)}% since the previous snapshot " +
                    $"({previousLive.Value} → {snapshot.Live}); the limit is " +
                    $"{Num(auditOptions.MaxLossPercentBetweenSnapshots, 0)}%.",
                    []));
        }

        // ── Thresholds ────────────────────────────────────────────────────────

        if (snapshot.RejectedMergeClustersRepeated > 0)
            violations.Add(new MemoryAuditInvariantViolation(
                NoRepeatedRejection,
                $"{snapshot.RejectedMergeClustersRepeated} merge cluster(s) have been rejected on " +
                $"{auditOptions.RepeatedRejectionRuns} or more consecutive runs — consolidation is retrying " +
                "work it cannot complete.",
                []));

        // Null means the window between runs was too short to measure a rate over, which is
        // not the same as a rate of zero and must not be thresholded either way.
        if (snapshot.NetGrowthPerDay is { } growth && growth > auditOptions.MaxNetGrowthPerDay)
            violations.Add(new MemoryAuditInvariantViolation(
                NetGrowthThreshold,
                $"The corpus is growing {Num(growth, 1)} entries/day, above the " +
                $"{Num(auditOptions.MaxNetGrowthPerDay, 0)}/day you set — saves are outpacing consolidation.",
                []));

        if (snapshot.MaxChainDepth > auditOptions.MaxMergeChainDepth)
            violations.Add(new MemoryAuditInvariantViolation(
                ChainDepthThreshold,
                $"A live entry sits at the end of a {snapshot.MaxChainDepth}-deep merge chain " +
                $"(limit {auditOptions.MaxMergeChainDepth}); it is model prose generated from model prose.",
                []));

        if (elapsedDays > 0)
        {
            var rejectedPerWeek = snapshot.RejectedMergeSourcesSinceLast * 7.0 / elapsedDays;
            if (rejectedPerWeek > auditOptions.MaxRejectedMergesPerWeek)
                violations.Add(new MemoryAuditInvariantViolation(
                    RejectedMergesThreshold,
                    $"Merge rejections are running at {Num(rejectedPerWeek, 1)}/week, above the " +
                    $"{auditOptions.MaxRejectedMergesPerWeek}/week you set.",
                    []));
        }

        return violations;
    }

    /// <summary>
    /// Worst finding wins. Nothing found means healthy — the audit does not manufacture a
    /// warning just to look busy.
    /// </summary>
    internal static string ComputeStatus(IReadOnlyList<MemoryAuditInvariantViolation> violations)
    {
        if (violations.Count == 0) return MemoryAuditStatuses.Healthy;
        return violations.Any(v => AlertInvariants.Contains(v.Name))
            ? MemoryAuditStatuses.Alert
            : MemoryAuditStatuses.Warning;
    }

    /// <summary>
    /// A fixed-point number with a dot, whatever the host's locale. These strings are stored in
    /// the JSON trend rows and read back by the sidecar, so a decimal comma would change the
    /// on-disk record.
    /// </summary>
    private static string Num(double value, int decimals) =>
        value.ToString(decimals == 0 ? "F0" : "F1", CultureInfo.InvariantCulture);

    /// <summary>When a merge was produced, per its own provenance stamp.</summary>
    private static DateTimeOffset? MergedAt(MemoryEntry entry)
    {
        if (entry.Metadata is not { } meta) return null;
        if (!meta.TryGetValue(DreamService.MergedAtKey, out var raw)) return null;
        return DateTimeOffset.TryParse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The replacement id an archived merge source points at, or null.</summary>
    private static string? MergedIntoTarget(MemoryEntry entry)
    {
        if (entry.ArchiveReason is not { } reason) return null;
        if (!reason.StartsWith(DreamService.MergedIntoReasonPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var target = reason[DreamService.MergedIntoReasonPrefix.Length..].Trim();
        return string.IsNullOrEmpty(target) ? null : target;
    }

    private static IReadOnlyList<string> Cap(List<string> ids) =>
        ids.Count <= MemoryAuditAnalyzer.MaxIdsPerViolation
            ? ids
            : [.. ids.Take(MemoryAuditAnalyzer.MaxIdsPerViolation)];
}
