using System.Globalization;

namespace RockBot.Host;

/// <summary>
/// Turns one walk of the memory store, plus the previous run's state, into a
/// <see cref="MemoryAuditSnapshot"/>. Pure — no I/O, no clock, no LLM.
/// </summary>
/// <remarks>
/// Purity is what makes the August incident reproducible as a test: 148 live entries, then 109
/// with 74 ids simply gone. That scenario can be fed in as data and the analyzer must report 74
/// hard deletes and an alert, with nothing mocked.
/// </remarks>
internal static class MemoryAuditAnalyzer
{
    /// <summary>Reinforcement histogram buckets, in report order.</summary>
    private static readonly (string Label, int Min, int Max)[] ReinforcementBuckets =
    [
        ("1", 1, 1),
        ("2-4", 2, 4),
        ("5-9", 5, 9),
        ("10-19", 10, 19),
        ("20+", 20, int.MaxValue)
    ];

    /// <summary>How many ids a single invariant violation carries into the trend file.</summary>
    internal const int MaxIdsPerViolation = 20;

    /// <summary>How many categories the growth table reports.</summary>
    private const int TopCategories = 5;

    /// <summary>Category label used for entries with no category.</summary>
    internal const string Uncategorized = "(uncategorized)";

    /// <summary>
    /// Measures the corpus and produces both the public snapshot row and the private state the
    /// next run will compare against.
    /// </summary>
    /// <param name="walk">What the walker found on disk.</param>
    /// <param name="previous">The previous run's state, or null on a first run.</param>
    /// <param name="passLastRunAt">Dream-pass name → last completed time, from the dream-pass ledger.</param>
    /// <param name="processStarts">Host start times observed so far, newest last.</param>
    /// <param name="embeddingDupClusters">
    /// Cluster count from the store's own duplicate detector, or null when it has none or the
    /// probe failed.
    /// </param>
    /// <param name="vocabularyStoplistSize">Size of the merge-coverage common-word list.</param>
    /// <param name="eval">Most recent eval summary to carry forward, if any.</param>
    /// <param name="dreamOptions">Supplies the archive retention window and the high-value floor.</param>
    /// <param name="auditOptions">Thresholds and windows.</param>
    /// <param name="now">Agent-local time of this run.</param>
    /// <param name="snapshotId">Id to stamp on the snapshot and the state.</param>
    /// <param name="ct">Cancellation token — the near-duplicate sweep is the expensive part.</param>
    internal static (MemoryAuditSnapshot Snapshot, MemoryAuditState State) Analyze(
        MemoryStoreWalker.WalkResult walk,
        MemoryAuditState? previous,
        IReadOnlyDictionary<string, DateTimeOffset> passLastRunAt,
        IReadOnlyList<DateTimeOffset> processStarts,
        int? embeddingDupClusters,
        int vocabularyStoplistSize,
        MemoryAuditEvalSummary? eval,
        DreamOptions dreamOptions,
        MemoryAuditOptions auditOptions,
        DateTimeOffset now,
        string snapshotId,
        CancellationToken ct = default)
    {
        var entries = walk.Entries;
        var live = entries.Where(e => e.ArchivedAt is null).ToList();
        var archived = entries.Where(e => e.ArchivedAt is not null).ToList();

        var byId = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            byId[entry.Id] = entry;

        // ── Deltas against the previous run ───────────────────────────────────
        //
        // Everything here is keyed on ids, never on entry timestamps. A merged entry inherits
        // its earliest source's CreatedAt, so a timestamp-based count of creations reports zero
        // for exactly the churn this is meant to expose.
        var previousRows = previous?.Entries ?? [];
        var previousById = new Dictionary<string, MemoryAuditEntryRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in previousRows)
            previousById[row.Id] = row;

        var createdIds = previous is null
            ? []
            : byId.Keys.Where(id => !previousById.ContainsKey(id)).ToList();

        var archivedSinceIds = previous is null
            ? []
            : archived
                .Where(e => previousById.TryGetValue(e.Id, out var row) && !row.Archived)
                .Select(e => e.Id)
                .ToList();

        var goneIds = previousRows
            .Where(row => !byId.ContainsKey(row.Id))
            .ToList();

        // An id that vanished is explained by the retention purge only if it was already
        // archived last time AND its retention window had closed. No purge timestamp is
        // recorded anywhere, so this deterministic reconstruction is the whole explanation —
        // anything it cannot account for is a hard delete nobody asked for.
        var purged = 0;
        foreach (var row in goneIds)
        {
            var eligible = row.Archived
                && row.ArchivedAt is { } at
                && dreamOptions.MemoryArchiveRetention > TimeSpan.Zero
                && at + dreamOptions.MemoryArchiveRetention < now;

            if (eligible) purged++;
        }

        var previousLive = previousRows.Count(r => !r.Archived);
        var elapsedDays = previous is { } prev && now > prev.TakenAt
            ? (now - prev.TakenAt).TotalDays
            : 0;

        // A restart puts two snapshots minutes apart. Dividing a handful of ordinary saves by
        // that window annualizes them into the thousands and trips the growth threshold on every
        // deploy, so below the minimum window the rate is reported as unmeasurable rather than
        // as a number nobody should act on. The absolute counts above are unaffected.
        var rateWindowMet = previous is not null
            && elapsedDays > 0
            && elapsedDays >= auditOptions.MinRateWindow.TotalDays;

        double? netGrowthPerDay = rateWindowMet
            ? Math.Round((live.Count - previousLive) / elapsedDays, 2)
            : null;

        var reinforcedWithoutMerge = live.Count(e =>
            previousById.TryGetValue(e.Id, out var row)
            && e.ReinforcementCount > row.ReinforcementCount
            && MergedFromIds(e).Count == row.MergedFromCount);

        // ── Merge provenance ──────────────────────────────────────────────────
        var depths = ComputeChainDepths(byId, ct);
        var byDepth = new Dictionary<int, int>();
        var maxDepth = 0;
        foreach (var entry in live)
        {
            var depth = depths.GetValueOrDefault(entry.Id);
            if (depth <= 0) continue;
            byDepth[depth] = byDepth.GetValueOrDefault(depth) + 1;
            if (depth > maxDepth) maxDepth = depth;
        }

        // Keyed by depth but ordered numerically. The keys are strings so the row serializes as
        // a JSON object, and insertion order is what both the report and the raw row show — a
        // histogram that reads "1, 3, 4, 2, 7" is harder to scan than one that counts upward.
        var histogram = byDepth
            .OrderBy(kv => kv.Key)
            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);

        // ── Near-duplicates ───────────────────────────────────────────────────
        var pairs = ShingleSimilarity.FindNearDuplicatePairs(
            live, auditOptions.ShingleSize, auditOptions.NearDuplicateThreshold, ct);

        var nearDupEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            nearDupEntries.Add(pair.IdA);
            nearDupEntries.Add(pair.IdB);
        }

        // ── Rejected merges ───────────────────────────────────────────────────
        //
        // Only rejections stamped since the previous run count. A stamp that has not moved
        // describes a rejection an earlier run already reported.
        var rejectionCutoff = previous?.TakenAt;
        var rejectedSourceIds = new List<string>();
        var clustersThisRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (entry.Metadata is not { } meta) continue;
            if (!meta.TryGetValue(DreamService.ConsolidationRejectedAtKey, out var rawAt)) continue;
            if (!DateTimeOffset.TryParse(rawAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var rejectedAt))
                continue;
            if (rejectionCutoff is { } cutoff && rejectedAt <= cutoff) continue;

            rejectedSourceIds.Add(entry.Id);
            if (meta.TryGetValue(DreamService.ConsolidationRejectedClusterKey, out var cluster)
                && !string.IsNullOrWhiteSpace(cluster))
                clustersThisRun.Add(cluster);
        }

        var clusterRuns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cluster in clustersThisRun)
        {
            var carried = previous?.RejectedClusterRuns.TryGetValue(cluster, out var count) == true ? count : 0;
            clusterRuns[cluster] = carried + 1;
        }

        var repeatedClusters = clusterRuns.Count(kv => kv.Value >= auditOptions.RepeatedRejectionRuns);

        // ── Dream cadence ─────────────────────────────────────────────────────
        var dreamPassesRun = previous is { } p
            ? passLastRunAt.Count(kv => kv.Value > p.TakenAt)
            : 0;

        passLastRunAt.TryGetValue(DreamService.ConsolidationLedgerPassName, out var consolidationLastRun);

        var restarts = previous is { } prevState
            ? processStarts.Count(t => t > prevState.TakenAt)
            : 0;

        // ── Purge outlook ─────────────────────────────────────────────────────
        var purgeHorizon = now + TimeSpan.FromDays(auditOptions.PurgeWarningDays);
        var dueSoon = dreamOptions.MemoryArchiveRetention > TimeSpan.Zero
            ? archived.Where(e => e.ArchivedAt!.Value + dreamOptions.MemoryArchiveRetention <= purgeHorizon).ToList()
            : [];

        var purgeOutlook = new MemoryAuditPurgeOutlook(
            auditOptions.PurgeWarningDays,
            dueSoon.Count,
            dueSoon.Count(e => DreamService.IsProtectedFromPruning(e, dreamOptions)));

        // ── Category movement ─────────────────────────────────────────────────
        //
        // Attributed from the entries that still exist. Hard-deleted entries carry their
        // category away with them, so a corpus losing a whole category shows up in the
        // hard-delete count rather than here — which is the more alarming of the two anyway.
        var growth = new Dictionary<string, (int Created, int Archived)>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in createdIds)
        {
            var key = CategoryOf(byId[id]);
            var current = growth.GetValueOrDefault(key);
            growth[key] = (current.Created + 1, current.Archived);
        }
        foreach (var id in archivedSinceIds)
        {
            var key = CategoryOf(byId[id]);
            var current = growth.GetValueOrDefault(key);
            growth[key] = (current.Created, current.Archived + 1);
        }

        var topCategories = growth
            .Select(kv => new MemoryAuditCategoryGrowth(
                kv.Key, kv.Value.Created, kv.Value.Archived, kv.Value.Created - kv.Value.Archived))
            .OrderByDescending(g => Math.Abs(g.Net))
            .ThenByDescending(g => g.Created)
            .ThenBy(g => g.Category, StringComparer.Ordinal)
            .Take(TopCategories)
            .ToList();

        // ── Reinforcement histogram ───────────────────────────────────────────
        var reinforcement = new Dictionary<string, int>();
        foreach (var (label, min, max) in ReinforcementBuckets)
        {
            var count = live.Count(e => e.ReinforcementCount >= min && e.ReinforcementCount <= max);
            if (count > 0) reinforcement[label] = count;
        }

        var snapshot = new MemoryAuditSnapshot
        {
            SnapshotId = snapshotId,
            TakenAt = now,
            PreviousTakenAt = previous?.TakenAt,
            Live = live.Count,
            Archived = archived.Count,
            MalformedFiles = walk.MalformedFiles,
            CreatedSinceLast = createdIds.Count,
            ArchivedSinceLast = archivedSinceIds.Count,
            HardDeletedSinceLast = goneIds.Count,
            PurgedSinceLast = purged,
            HardDeletedOutsidePurge = goneIds.Count - purged,
            NetGrowthPerDay = netGrowthPerDay,
            MergeChainDepth = histogram,
            MaxChainDepth = maxDepth,
            NearDupPairs = pairs.Count,
            NearDupEntries = nearDupEntries.Count,
            EmbeddingDupClusters = embeddingDupClusters,
            Reinforcement = reinforcement,
            ReinforcedWithoutMergeSinceLast = reinforcedWithoutMerge,
            RejectedMergeSourcesSinceLast = rejectedSourceIds.Count,
            RejectedMergeClustersRepeated = repeatedClusters,
            DreamPassesRunSinceLast = dreamPassesRun,
            ConsolidationLastRunAt = consolidationLastRun == default ? null : consolidationLastRun,
            RestartsSinceLast = restarts,
            EmptyCategoryDirs = walk.EmptyCategoryDirs,
            Purge = purgeOutlook,
            TopCategoriesByGrowth = topCategories,
            VocabularyStoplistSize = vocabularyStoplistSize,
            Eval = eval
        };

        var violations = MemoryAuditInvariants.Check(
            entries, snapshot, dreamOptions, auditOptions, now,
            rateWindowMet ? elapsedDays : 0,
            previous is null ? null : previousLive);
        snapshot = snapshot with
        {
            Invariants = violations,
            Status = MemoryAuditInvariants.ComputeStatus(violations)
        };

        var state = new MemoryAuditState
        {
            TakenAt = now,
            SnapshotId = snapshotId,
            Entries = [.. entries.Select(e => new MemoryAuditEntryRow(
                e.Id,
                e.ArchivedAt is not null,
                e.ArchivedAt,
                e.ReinforcementCount,
                MergedFromIds(e).Count,
                e.Category))],
            RejectedClusterRuns = clusterRuns,
            RejectedSourceIds = rejectedSourceIds,
            ProcessStarts = [.. processStarts.Where(t => t >= now - MemoryAuditState.ProcessStartRetention)],
            LastEvalAt = previous?.LastEvalAt,
            LastEvalFingerprint = previous?.LastEvalFingerprint,
            LastDigestAt = previous?.LastDigestAt
        };

        return (snapshot, state);
    }

    /// <summary>Category path for grouping, with a stable label for the null case.</summary>
    internal static string CategoryOf(MemoryEntry entry) =>
        string.IsNullOrWhiteSpace(entry.Category) ? Uncategorized : entry.Category;

    /// <summary>
    /// The ids an entry's merge provenance names, or an empty list when it is not a merge.
    /// </summary>
    internal static IReadOnlyList<string> MergedFromIds(MemoryEntry entry)
    {
        if (entry.Metadata is not { } meta) return [];
        if (!meta.TryGetValue(DreamService.MergedFromKey, out var raw) || string.IsNullOrWhiteSpace(raw))
            return [];

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// Longest merge-provenance walk ending at each entry. A non-merge is depth 0; a merge of
    /// raw saves is 1; a merge of merges is 2 and up.
    /// </summary>
    /// <remarks>
    /// Depth is the honest measure of how far a fact has drifted from anything a human or a tool
    /// actually observed. A source that has been purged terminates the walk at depth 0 rather
    /// than being guessed at — provenance is a recovery aid with a retention window, and
    /// pretending to know the depth of something that no longer exists would inflate exactly the
    /// number an operator would act on.
    /// </remarks>
    internal static Dictionary<string, int> ComputeChainDepths(
        IReadOnlyDictionary<string, MemoryEntry> byId,
        CancellationToken ct = default)
    {
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in byId.Keys)
        {
            ct.ThrowIfCancellationRequested();
            Depth(id);
        }

        return depths;

        int Depth(string id)
        {
            if (depths.TryGetValue(id, out var known)) return known;

            // Provenance is written by an LLM-driven pass; a cycle is unlikely but must not
            // become a stack overflow in a diagnostic.
            if (!visiting.Add(id)) return 0;

            var depth = 0;
            if (byId.TryGetValue(id, out var entry))
            {
                foreach (var source in MergedFromIds(entry))
                {
                    if (!byId.ContainsKey(source)) continue;
                    depth = Math.Max(depth, 1 + Depth(source));
                }

                // A merge whose sources have all been purged still is a merge.
                if (depth == 0 && MergedFromIds(entry).Count > 0)
                    depth = 1;
            }

            visiting.Remove(id);
            depths[id] = depth;
            return depth;
        }
    }
}
