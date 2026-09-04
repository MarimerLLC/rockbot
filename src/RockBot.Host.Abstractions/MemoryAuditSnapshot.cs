namespace RockBot.Host;

/// <summary>
/// File names and defaults for the memory audit's own on-disk layout. Shared with the
/// introspection sidecar, which reads these files directly rather than through the host.
/// </summary>
public static class MemoryAuditFiles
{
    /// <summary>Default audit directory, relative to <see cref="AgentProfileOptions.BasePath"/>.</summary>
    public const string DefaultBasePath = "memory-audit";

    /// <summary>Append-only trend file — one <see cref="MemoryAuditSnapshot"/> per line.</summary>
    public const string SnapshotsFile = "snapshots.jsonl";

    /// <summary>Private carry-over between runs. Not part of the trend; not a public surface.</summary>
    public const string StateFile = "state.json";

    /// <summary>Most recent plain-language report.</summary>
    public const string LatestReport = "latest.md";

    /// <summary>Most recent LLM-judged sample eval.</summary>
    public const string EvalLatest = "eval-latest.json";

    /// <summary>Marker file that stops dream consolidation. Present means paused.</summary>
    public const string ConsolidationPausedFile = "consolidation-paused.json";
}

/// <summary>Status values a <see cref="MemoryAuditSnapshot"/> can carry.</summary>
public static class MemoryAuditStatuses
{
    /// <summary>Every invariant held and no threshold was crossed.</summary>
    public const string Healthy = "healthy";

    /// <summary>A threshold was crossed, or a structural oddity was found. Nothing was lost.</summary>
    public const string Warning = "warning";

    /// <summary>Data was destroyed, or provenance points at something that no longer exists.</summary>
    public const string Alert = "alert";
}

/// <summary>
/// A single invariant that did not hold, named so an operator can look it up in
/// <c>docs/memory-audit.md</c>.
/// </summary>
/// <param name="Name">Stable kebab-case identifier, e.g. <c>merge-chain-unbroken</c>.</param>
/// <param name="Message">One sentence a human can act on.</param>
/// <param name="Ids">Entry ids involved, capped so a mass failure does not bloat the trend file.</param>
public sealed record MemoryAuditInvariantViolation(
    string Name,
    string Message,
    IReadOnlyList<string> Ids);

/// <summary>How much of the archive tier is about to be hard-deleted for good.</summary>
/// <param name="DueWithinDays">Look-ahead window this outlook covers.</param>
/// <param name="Count">Archived entries whose retention window closes inside it.</param>
/// <param name="HighValueCount">
/// The subset the high-value floor will hold back. Reported separately because the difference
/// between the two numbers is what a human would actually want to rescue.
/// </param>
public sealed record MemoryAuditPurgeOutlook(int DueWithinDays, int Count, int HighValueCount);

/// <summary>Net movement in one category since the previous snapshot.</summary>
/// <param name="Category">Category path, or <c>"(uncategorized)"</c>.</param>
/// <param name="Created">Entries that appeared.</param>
/// <param name="Archived">Entries that moved to the archive tier.</param>
/// <param name="Net">Created minus archived.</param>
public sealed record MemoryAuditCategoryGrowth(string Category, int Created, int Archived, int Net);

/// <summary>One judged sample from the weekly eval.</summary>
/// <param name="Category">Which sampling family it came from — merge, near-duplicate, high-reinforcement, ephemeral-archive.</param>
/// <param name="Ids">Entry ids the judge was shown.</param>
/// <param name="Sound">Whether the judge thought the stored outcome was right.</param>
/// <param name="Reason">The judge's one-line justification.</param>
public sealed record MemoryAuditEvalVerdict(
    string Category,
    IReadOnlyList<string> Ids,
    bool Sound,
    string? Reason);

/// <summary>Rolled-up eval rates, small enough to embed in every snapshot row.</summary>
/// <param name="EvaluatedAt">When the eval ran.</param>
/// <param name="Sampled">Total samples judged.</param>
/// <param name="Sound">How many the judge approved.</param>
/// <param name="SoundRate">Approved over sampled, 0..1.</param>
/// <param name="RateByCategory">Per-family approval rate, 0..1.</param>
public sealed record MemoryAuditEvalSummary(
    DateTimeOffset EvaluatedAt,
    int Sampled,
    int Sound,
    double SoundRate,
    IReadOnlyDictionary<string, double> RateByCategory);

/// <summary>
/// Full eval output, written to its own file. The snapshot carries only
/// <see cref="Summary"/>.
/// </summary>
/// <param name="Summary">Rolled-up rates.</param>
/// <param name="Verdicts">Per-sample verdicts, ids included so a finding can be chased.</param>
/// <param name="StoreFingerprint">
/// Hash of the corpus the eval ran against, so an unchanged store can skip the LLM call.
/// </param>
public sealed record MemoryAuditEvalResult(
    MemoryAuditEvalSummary Summary,
    IReadOnlyList<MemoryAuditEvalVerdict> Verdicts,
    string StoreFingerprint);

/// <summary>
/// One measurement of the memory store, taken by walking the files on disk rather than by
/// asking the store. Appended as a single JSON line to <c>snapshots.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything counted "since last" is measured against the previous run's private state file,
/// not against timestamps on the entries themselves. A merged entry inherits its earliest
/// source's <c>createdAt</c>, so counting creations by timestamp would silently under-report
/// exactly the churn this is meant to expose.
/// </para>
/// <para>
/// The record is a public contract: the introspection sidecar deserializes these rows straight
/// off the volume, so fields are added, never renamed or repurposed.
/// </para>
/// </remarks>
public sealed record MemoryAuditSnapshot
{
    /// <summary>Unique id for this run, referenced by the pause marker and the report.</summary>
    public required string SnapshotId { get; init; }

    /// <summary>
    /// What was measured. Fixed at <c>"memory"</c> today; present so a later audit of a
    /// different store can share the file format.
    /// </summary>
    public string Domain { get; init; } = "memory";

    /// <summary>When this run happened, in agent-local time.</summary>
    public required DateTimeOffset TakenAt { get; init; }

    /// <summary>When the previous run happened, or null on the first ever run.</summary>
    public DateTimeOffset? PreviousTakenAt { get; init; }

    /// <summary>Entries with no <see cref="MemoryEntry.ArchivedAt"/>.</summary>
    public int Live { get; init; }

    /// <summary>Entries still on disk with <see cref="MemoryEntry.ArchivedAt"/> set.</summary>
    public int Archived { get; init; }

    /// <summary>Files under the memory root that would not deserialize.</summary>
    public int MalformedFiles { get; init; }

    /// <summary>Ids present now that were absent at the previous run.</summary>
    public int CreatedSinceLast { get; init; }

    /// <summary>Ids that were live at the previous run and are archived now.</summary>
    public int ArchivedSinceLast { get; init; }

    /// <summary>Ids present at the previous run that are gone from disk entirely.</summary>
    public int HardDeletedSinceLast { get; init; }

    /// <summary>
    /// The subset of <see cref="HardDeletedSinceLast"/> the retention purge can account for —
    /// archived at the previous run and past the archive retention window.
    /// </summary>
    public int PurgedSinceLast { get; init; }

    /// <summary>
    /// Disappearances the purge cannot account for. This is the number the audit exists to
    /// keep at zero.
    /// </summary>
    public int HardDeletedOutsidePurge { get; init; }

    /// <summary>
    /// Change in live count divided by days elapsed, or null when the window between runs was
    /// shorter than <see cref="MemoryAuditOptions.MinRateWindow"/> and the rate would be an
    /// extrapolation rather than a measurement. Negative means the corpus shrank.
    /// </summary>
    public double? NetGrowthPerDay { get; init; }

    /// <summary>Histogram of merge-provenance chain depth across live entries, depth → count.</summary>
    public IReadOnlyDictionary<string, int> MergeChainDepth { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Deepest merge chain any live entry sits at the end of.</summary>
    public int MaxChainDepth { get; init; }

    /// <summary>Live entry pairs above <see cref="MemoryAuditOptions.NearDuplicateThreshold"/>.</summary>
    public int NearDupPairs { get; init; }

    /// <summary>Distinct live entries appearing in at least one such pair.</summary>
    public int NearDupEntries { get; init; }

    /// <summary>
    /// Clusters the store's own duplicate detector finds, when it has one. Null on a store with
    /// no embeddings or when the probe failed — distinct from zero, which means it looked and
    /// found none.
    /// </summary>
    public int? EmbeddingDupClusters { get; init; }

    /// <summary>Histogram of reinforcement counts across live entries, bucket label → count.</summary>
    public IReadOnlyDictionary<string, int> Reinforcement { get; init; } =
        new Dictionary<string, int>();

    /// <summary>
    /// Entries whose reinforcement count rose without their merge provenance changing — real
    /// re-observation rather than a merge summing its sources.
    /// </summary>
    public int ReinforcedWithoutMergeSinceLast { get; init; }

    /// <summary>Entries stamped as the source of a merge the coverage check refused.</summary>
    public int RejectedMergeSourcesSinceLast { get; init; }

    /// <summary>
    /// Merge clusters rejected on <see cref="MemoryAuditOptions.RepeatedRejectionRuns"/> or more
    /// consecutive runs — the treadmill shape, where the same cluster is proposed and refused
    /// forever.
    /// </summary>
    public int RejectedMergeClustersRepeated { get; init; }

    /// <summary>Dream passes whose ledger stamp moved since the previous run.</summary>
    public int DreamPassesRunSinceLast { get; init; }

    /// <summary>When memory consolidation last completed, per the dream-pass ledger.</summary>
    public DateTimeOffset? ConsolidationLastRunAt { get; init; }

    /// <summary>
    /// Process starts observed since the previous run. A restart storm is the documented cause
    /// of a consolidation storm, so it belongs next to the archive counts.
    /// </summary>
    public int RestartsSinceLast { get; init; }

    /// <summary>Category directories under the memory root holding no entries.</summary>
    public int EmptyCategoryDirs { get; init; }

    /// <summary>What the retention purge is about to destroy.</summary>
    public MemoryAuditPurgeOutlook Purge { get; init; } = new(0, 0, 0);

    /// <summary>Categories that moved most since the previous run, largest absolute net first.</summary>
    public IReadOnlyList<MemoryAuditCategoryGrowth> TopCategoriesByGrowth { get; init; } = [];

    /// <summary>
    /// Size of the merge-coverage vocabulary's common-word list. Tracked because growing the
    /// stoplist is how a coverage check is quietly weakened.
    /// </summary>
    public int VocabularyStoplistSize { get; init; }

    /// <summary>Invariants that did not hold on this run.</summary>
    public IReadOnlyList<MemoryAuditInvariantViolation> Invariants { get; init; } = [];

    /// <summary>One of <see cref="MemoryAuditStatuses"/>.</summary>
    public string Status { get; init; } = MemoryAuditStatuses.Healthy;

    /// <summary>Most recent eval summary, when one exists. Carried forward between eval runs.</summary>
    public MemoryAuditEvalSummary? Eval { get; init; }
}
