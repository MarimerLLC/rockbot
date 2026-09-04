namespace RockBot.Host;

/// <summary>
/// Options for the read-only memory audit. Bound from the <c>MemoryAudit</c> configuration
/// section.
/// </summary>
/// <remarks>
/// <para>
/// The audit exists because every finding about memory management so far came from a human
/// pulling the PVC and parsing Loki after the fact. Loki keeps roughly a week, the dream-pass
/// ledger records only last-run timestamps, and the dream cycle's own log lines looked healthy
/// through every incident. Measuring the store on disk, on its own schedule, into a file that
/// outlives log retention is the only way the agent can answer "are you losing memories?"
/// without a human doing forensics.
/// </para>
/// <para>
/// Nothing here writes to the memory store. The one exception to read-only behaviour is
/// <see cref="PauseConsolidationOnAlert"/>, which drops a marker file that the dream cycle
/// consults — and that file only ever stops work, never destroys anything.
/// </para>
/// </remarks>
public sealed class MemoryAuditOptions
{
    /// <summary>Whether the audit service runs at all. Defaults to <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cron schedule for the measurement run. Defaults to <c>"0 4 * * *"</c> — once daily at
    /// 04:00 agent-local, well clear of the 12-hourly dream cycle so the two do not queue
    /// behind each other on the work serializer.
    /// </summary>
    public string CronSchedule { get; set; } = "0 4 * * *";

    /// <summary>
    /// Delay after host start before the first run. Defaults to 10 minutes — long enough that
    /// a restart storm does not produce a snapshot per deploy.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Directory holding the audit's own files. When relative, resolved under
    /// <see cref="AgentProfileOptions.BasePath"/>. Defaults to <c>"memory-audit"</c>.
    /// </summary>
    public string BasePath { get; set; } = MemoryAuditFiles.DefaultBasePath;

    /// <summary>
    /// How long snapshot rows are kept in <c>snapshots.jsonl</c>. Defaults to 400 days.
    /// </summary>
    /// <remarks>
    /// Deliberately far longer than the dream cycle's log retention. The trend file is the
    /// whole point of the audit: a corpus that loses 2% a month looks fine in any seven-day
    /// window and is catastrophic across a year. One row per day at a few hundred bytes is
    /// under 100 KB at this retention.
    /// </remarks>
    public TimeSpan SnapshotRetention { get; set; } = TimeSpan.FromDays(400);

    /// <summary>
    /// Jaccard overlap at or above which two live entries are counted as near-duplicates of
    /// each other. Defaults to 0.3.
    /// </summary>
    /// <remarks>
    /// Not comparable to the save-time dedupe thresholds — this is Jaccard overlap over word
    /// n-gram sets, not cosine or token overlap, and it is a health metric rather than a
    /// decision to fold anything. At the default <see cref="ShingleSize"/> it measures
    /// near-verbatim duplication: the same text saved twice, which is what a failing save-time
    /// dedupe looks like. Lower <see cref="ShingleSize"/> to catch rephrasings too, at the cost
    /// of pairing entries that merely share boilerplate.
    /// </remarks>
    public double NearDuplicateThreshold { get; set; } = 0.3;

    /// <summary>Word n-gram size for the near-duplicate shingle sets. Defaults to 6.</summary>
    public int ShingleSize { get; set; } = 6;

    /// <summary>
    /// Reinforcement count at or above which an entry is sampled as "high reinforcement" by the
    /// weekly eval. Defaults to 20.
    /// </summary>
    public int HighReinforcementFloor { get; set; } = 20;

    /// <summary>
    /// How far ahead the purge outlook looks, in days. Defaults to 7 — one warning window
    /// before archived entries are hard-deleted for good.
    /// </summary>
    public int PurgeWarningDays { get; set; } = 7;

    // ── Thresholds ────────────────────────────────────────────────────────────

    /// <summary>
    /// Shortest interval between two runs over which a per-day or per-week rate is considered
    /// measurable. Defaults to 12 hours.
    /// </summary>
    /// <remarks>
    /// A restart produces two snapshots minutes apart. Extrapolating that window to a daily
    /// rate is arithmetic, not information: six ordinary saves seven minutes apart annualize to
    /// 1311 entries/day and trip <see cref="MaxNetGrowthPerDay"/> every single deploy. Below
    /// this window the rate is reported as unmeasurable and the rate-based invariants do not
    /// run — the absolute counts in the same snapshot are unaffected and stay accurate.
    /// </remarks>
    public TimeSpan MinRateWindow { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Net live-entry growth per day above which the corpus is flagged as diverging rather
    /// than converging. Defaults to 5. Only evaluated over windows of at least
    /// <see cref="MinRateWindow"/>.
    /// </summary>
    public double MaxNetGrowthPerDay { get; set; } = 5;

    /// <summary>
    /// Longest merge-provenance chain a live entry may sit at the end of. Defaults to 2.
    /// </summary>
    /// <remarks>
    /// A merge of merges is LLM prose generated from LLM prose. Each hop is another chance to
    /// drop a specific with nothing left to compare against, so depth is the metric that
    /// distinguishes healthy deduplication from a consolidation treadmill.
    /// </remarks>
    public int MaxMergeChainDepth { get; set; } = 2;

    /// <summary>
    /// Rejected-merge sources per week above which consolidation is flagged as fighting the
    /// coverage check. Defaults to 5.
    /// </summary>
    public int MaxRejectedMergesPerWeek { get; set; } = 5;

    /// <summary>
    /// Entries that may vanish between snapshots without being purge-eligible. Defaults to 0 —
    /// a hard delete outside the retention purge is the failure mode the whole audit exists to
    /// catch, so any occurrence is an alert.
    /// </summary>
    public int MaxHardDeletesOutsidePurge { get; set; }

    /// <summary>
    /// Percentage drop in live entries between two consecutive snapshots that raises an alert.
    /// Defaults to 10.
    /// </summary>
    public double MaxLossPercentBetweenSnapshots { get; set; } = 10;

    /// <summary>
    /// Consecutive audit runs a merge cluster must be rejected on before it is reported as a
    /// stuck cluster. Defaults to 3.
    /// </summary>
    public int RepeatedRejectionRuns { get; set; } = 3;

    // ── LLM-judged sample eval ────────────────────────────────────────────────

    /// <summary>Whether the weekly LLM-judged sample eval runs. Defaults to <c>true</c>.</summary>
    public bool EvalEnabled { get; set; } = true;

    /// <summary>
    /// Cron schedule for the eval. Defaults to <c>"0 5 * * 0"</c> — Sunday 05:00, an hour after
    /// that day's measurement run.
    /// </summary>
    public string EvalCronSchedule { get; set; } = "0 5 * * 0";

    /// <summary>Model tier for the eval judge. Defaults to <see cref="ModelTier.Balanced"/>.</summary>
    public ModelTier EvalModelTier { get; set; } = ModelTier.Balanced;

    /// <summary>Maximum samples judged per category per eval run. Defaults to 10.</summary>
    public int EvalSampleSize { get; set; } = 10;

    /// <summary>
    /// How far back the eval draws its merge and ephemeral-archive samples. Defaults to 14 days.
    /// </summary>
    public TimeSpan EvalWindow { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Judge directive file. When relative, resolved under
    /// <see cref="AgentProfileOptions.BasePath"/>. Defaults to <c>"memory-audit.md"</c>.
    /// </summary>
    public string EvalDirectivePath { get; set; } = "memory-audit.md";

    // ── Surfacing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a run whose status is not <c>healthy</c> pushes an unsolicited message to the
    /// <see cref="WellKnownSessions.ScheduledSystem"/> session. Defaults to <c>true</c>.
    /// </summary>
    /// <remarks>
    /// A healthy run is silent. The audit is worth having precisely because nobody reads a
    /// daily "all clear", and a channel that speaks only when something is wrong stays worth
    /// reading.
    /// </remarks>
    public bool AlertOnAttention { get; set; } = true;

    /// <summary>
    /// Optional cron schedule for pushing the full report regardless of status. Null (the
    /// default) means only alerts are pushed.
    /// </summary>
    public string? DigestCronSchedule { get; set; }

    /// <summary>
    /// Whether the latest report is also copied to the shared volume so it can be opened
    /// outside the agent. Defaults to <c>true</c>.
    /// </summary>
    public bool CopyReportToShared { get; set; } = true;

    /// <summary>
    /// Destination for the shared copy. Defaults to the exports tree, which the shared-volume
    /// cleanup CronJob sweeps on its own TTL — the audit re-creates its subdirectory on every
    /// run so a sweep between runs is harmless.
    /// </summary>
    public string SharedReportDirectory { get; set; } = "/rockbot/shared/exports/memory-audit";

    // ── Circuit breaker ───────────────────────────────────────────────────────

    /// <summary>
    /// Whether a catastrophic-loss finding writes the marker file that stops dream
    /// consolidation. Defaults to <c>false</c> — opt-in, because a false positive stops the
    /// only thing keeping the corpus from growing without bound.
    /// </summary>
    /// <remarks>
    /// The auditor never clears the marker. Resuming is a human decision (or an explicit
    /// <c>resume_memory_consolidation</c> tool call), because the point of the pause is that
    /// somebody looks at what happened before the same pass runs again.
    /// </remarks>
    public bool PauseConsolidationOnAlert { get; set; }
}
