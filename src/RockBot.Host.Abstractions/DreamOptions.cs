namespace RockBot.Host;

/// <summary>
/// Options for the periodic memory consolidation service (dreaming).
/// </summary>
public sealed class DreamOptions
{
    /// <summary>Whether dreaming is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to wait after startup before the first dream cycle.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cron expression (5-field or 6-field with seconds) controlling how often dream cycles run.
    /// Evaluated in the agent's configured timezone (see <c>Agent:Timezone</c>).
    /// Default: every 12 hours at the top of the hour.
    /// </summary>
    public string CronSchedule { get; set; } = "0 */12 * * *";

    /// <summary>
    /// Whether a dream cycle blocked by other agent work is retried with backoff instead of being
    /// dropped until the next cron occurrence. Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// The work serializer is acquired non-blockingly, so a cycle that fires while a patrol,
    /// scheduled task, or user turn holds the slot used to be abandoned outright — the next
    /// attempt was a full <see cref="CronSchedule"/> period away. That is not a rare collision:
    /// patrols run on their own schedule, and one that habitually overlaps a cron slot can cost
    /// the same dream every day, indefinitely, with only an info line to show for it.
    /// </remarks>
    public bool DeferDreamOnContention { get; set; } = true;

    /// <summary>
    /// Delay before the first retry of a dream cycle that could not acquire the work slot.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan DreamContentionRetryInitialDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Growth factor applied to each successive contention retry delay. Default: 2.0.
    /// </summary>
    public double DreamContentionRetryMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Ceiling on a single contention retry delay. Default: 1 hour — long enough that a busy
    /// agent is not polled constantly, short enough that the cycle still lands the same day.
    /// </summary>
    public TimeSpan DreamContentionRetryMaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many times a blocked cycle is retried before giving up and waiting for the next cron
    /// occurrence. Default: 6, which with the default delays spans roughly three hours
    /// (5m, 10m, 20m, 40m, 1h, 1h). A retry is also abandoned early if the next scheduled cycle
    /// would arrive first, so this never delays the schedule.
    /// </summary>
    public int DreamContentionMaxRetries { get; set; } = 6;

    /// <summary>
    /// Whether corpus-wide dream passes skip their LLM call when the input they would send has
    /// not changed since the last time they ran. Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Several passes are corpus-wide rather than delta-driven: skill consolidation ships the
    /// whole skill catalog, graph consolidation the whole graph, the contradiction sweep the
    /// whole claim/feedback corpus, identity reflection its full experiential context. Ungated,
    /// each re-asks the model the same question about the same bytes on every cycle — twice a
    /// day at the default <see cref="CronSchedule"/> — and the bill scales with corpus size
    /// rather than with how much the agent actually did.
    /// </para>
    /// <para>
    /// The fingerprint covers the corpus itself, not statistics derived from it. Skill usage
    /// counts and co-occurrence tallies are 30-day rolling annotations that drift on their own
    /// as old events age out; treating that drift as a change would keep an idle agent dreaming
    /// for a month after its last conversation. Set to <c>false</c> to restore the previous
    /// run-every-cycle behaviour.
    /// </para>
    /// </remarks>
    public bool DreamPassChangeGateEnabled { get; set; } = true;

    /// <summary>
    /// Longest a gated dream pass may go without actually running, however unchanged its inputs
    /// look. Default: 7 days. Set to <see cref="TimeSpan.Zero"/> or negative to make the change
    /// gate absolute.
    /// </summary>
    /// <remarks>
    /// Some directives are time-dependent in ways an input hash cannot see — graph consolidation
    /// prunes entities by staleness, so an untouched graph still becomes prunable purely through
    /// the passage of time. Without this floor, gating those passes on content would quietly
    /// switch such behaviour off. With it, an idle agent runs them once a week instead of
    /// fourteen times, and nothing stops firing altogether.
    /// </remarks>
    public TimeSpan DreamPassMaxSkipInterval { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Which LLM tier the dream passes run on. Defaults to <see cref="ModelTier.Balanced"/>,
    /// matching the previous hardcoded behaviour.
    /// <para>
    /// Dream passes are structured-extraction work — read a transcript, return JSON — which is
    /// a very different job from whatever the agent does conversationally. Pointing this at a
    /// different tier lets an agent run its conversation on a model chosen for voice while
    /// consolidating memory on one chosen for instruction-following and reliable JSON.
    /// </para>
    /// </summary>
    public ModelTier ModelTier { get; set; } = ModelTier.Balanced;

    /// <summary>
    /// Path to the memory consolidation directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string DirectivePath { get; set; } = "dream.md";

    /// <summary>
    /// Path to the skill consolidation directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string SkillDirectivePath { get; set; } = "skill-dream.md";

    /// <summary>
    /// Path to the skill optimization directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// Used by the post-consolidation pass that improves skills associated with poor sessions.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string SkillOptimizeDirectivePath { get; set; } = "skill-optimize.md";

    /// <summary>
    /// Whether the preference inference pass (requires <see cref="IConversationLog"/>) is enabled.
    /// </summary>
    public bool PreferenceInferenceEnabled { get; set; } = true;

    /// <summary>
    /// Path to the preference inference directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string PreferenceDirectivePath { get; set; } = "pref-dream.md";

    /// <summary>Whether the skill gap detection pass is enabled.</summary>
    public bool SkillGapEnabled { get; set; } = true;

    /// <summary>
    /// Path to the skill gap detection directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string SkillGapDirectivePath { get; set; } = "skill-gap.md";

    /// <summary>Whether the episode extraction pass (requires <see cref="IConversationLog"/>) is enabled.</summary>
    public bool EpisodeExtractionEnabled { get; set; } = true;

    /// <summary>
    /// Path to the episode extraction directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string EpisodeDirectivePath { get; set; } = "episode-dream.md";

    /// <summary>
    /// Whether the memory consolidation pass is enabled — the pass that merges duplicate
    /// long-term memory entries, re-scores importance, and prunes ephemeral ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Worth turning off when entry fidelity matters more than tidiness. Consolidation is the
    /// one pass that <em>rewrites</em> stored memories rather than only adding or deleting
    /// them, so it is also the only place a weaker or more creative dream model can introduce
    /// detail that was never in any source entry. Mining, episode and entity extraction all
    /// derive from the conversation log and can be checked against it; a consolidated entry
    /// has no such anchor, and any embellishment it picks up is indistinguishable from a real
    /// fact on the next pass — which then feeds it back in as its own input.
    /// </para>
    /// <para>
    /// With this off, duplicate and near-duplicate entries accumulate instead of being merged.
    /// Importance re-scoring and the decay pass both stop as well, since both run inside
    /// consolidation. That is the trade: a larger, redundant store with static importance,
    /// whose entries are all traceable to something that was actually said.
    /// </para>
    /// </remarks>
    public bool MemoryConsolidationEnabled { get; set; } = true;

    /// <summary>
    /// How long entries archived by consolidation are kept before the purge pass hard-deletes
    /// them. Archived entries are hidden from recall but stay on disk and remain retrievable
    /// by id, so this is the window in which a bad merge or a wrong ephemeral call can still
    /// be undone. Set to <see cref="TimeSpan.Zero"/> or negative to keep archived entries
    /// forever. Default: 90 days.
    /// </summary>
    /// <remarks>
    /// Requires the store to implement <see cref="IArchivedMemoryMaintenance"/>; with a store
    /// that does not, <see cref="ILongTermMemory.ArchiveAsync"/> falls back to a hard delete
    /// and this setting has nothing to purge.
    /// </remarks>
    public TimeSpan MemoryArchiveRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Similarity (0..1) at which two memory entries are treated as possible duplicates and
    /// become eligible for the consolidation pass. Cosine over embeddings where the store has
    /// them, a lexical measure otherwise. Default: 0.88.
    /// </summary>
    /// <remarks>
    /// This is the main dial on how much consolidation is allowed to touch. Lower values feed
    /// it more pairs and merge more aggressively; higher values restrict it to obvious
    /// restatements. It does not affect entries that are new or changed since the last pass —
    /// those are always eligible regardless of similarity.
    /// </remarks>
    public double ConsolidationSimilarityThreshold { get; set; } = 0.88;

    /// <summary>
    /// Ceiling on how many entries may appear in a single near-duplicate cluster. Larger
    /// clusters are split into chunks rather than dropped. Default: 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This bounds <em>eligibility</em>, not merge size. Clusters decide which entries the
    /// model is shown; once shown, it may propose a merge over any subset of them, so a pass
    /// can and does produce merges with more sources than this value. Observed in production:
    /// a 13-source merge under the default of 3.
    /// </para>
    /// <para>
    /// Large merges are constrained by the coverage check instead, which is the better tool
    /// for the job — it judges whether detail actually survived rather than guessing from a
    /// count. The same production cycle that produced the 13-source merge had it accepted
    /// (every specific preserved) while rejecting a 6-source merge that dropped 28.
    /// </para>
    /// </remarks>
    public int ConsolidationMaxClusterSize { get; set; } = 3;

    /// <summary>
    /// Importance at or above which an entry may not be pruned outright by consolidation.
    /// Merging is still permitted, since a merge preserves the content. Default: 0.80.
    /// </summary>
    /// <remarks>
    /// A deterministic floor rather than prompt guidance. The directive already told the model
    /// that reinforcement signals importance, and it deleted 0.99-scored entries anyway.
    /// </remarks>
    public float PruningProtectionImportance { get; set; } = 0.80f;

    /// <summary>
    /// Reinforcement count at or above which an entry may not be pruned outright by
    /// consolidation. Merging is still permitted. Default: 5.
    /// </summary>
    /// <remarks>
    /// Repeated independent observation is the strongest evidence available that a fact
    /// matters — it is exactly the signal that should have protected the entries a live corpus
    /// lost at 214, 106 and 80 observations.
    /// </remarks>
    public int PruningProtectionReinforcementCount { get; set; } = 5;

    /// <summary>
    /// Path to the merge-coverage vocabulary file, relative to
    /// <see cref="AgentProfileOptions.BasePath"/>. Re-read at the top of every dream cycle, so
    /// edits take effect without a restart. When absent, a generic-English baseline is used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Controls which capitalized words the coverage check treats as ordinary language rather
    /// than as detail a merge must preserve. This is deployment-specific: the words an
    /// operational assistant can safely ignore are not the words a storytelling agent can.
    /// </para>
    /// <para>
    /// Shape — both fields optional:
    /// <code>
    /// {
    ///   "extraCommonWords":    ["briefing", "triage"],
    ///   "alwaysSpecificWords": ["May", "Will", "Rose"]
    /// }
    /// </code>
    /// <c>alwaysSpecificWords</c> takes precedence, and matters most for agents whose characters
    /// or people collide with ordinary English — without it, a character named May or Will is
    /// silently stripped of coverage protection.
    /// </para>
    /// </remarks>
    public string MergeCoverageVocabularyPath { get; set; } = "merge-coverage-vocabulary.json";

    /// <summary>Whether the memory mining pass (requires <see cref="IConversationLog"/>) is enabled.</summary>
    public bool MemoryMiningEnabled { get; set; } = true;

    /// <summary>
    /// Path to the memory mining directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string MemoryMiningDirectivePath { get; set; } = "memory-mining.md";

    /// <summary>
    /// Whether the cast voice enrichment pass is enabled. Off by default, and inert until
    /// <see cref="CastVoiceCategory"/> is also configured: it is only meaningful for agents that
    /// maintain a character corpus in a dedicated memory category.
    /// </summary>
    /// <remarks>
    /// Unlike every other memory pass, this one reads <em>existing memory</em> rather than the
    /// conversation log, because the gap it fills is in characters whose scenes have long since
    /// scrolled out of the transcript. It is also the one pass permitted to <em>derive</em>
    /// rather than only record — a voice is inferred from a character's recorded background when
    /// no line of theirs survives. That licence is bounded by the directive: derive speech habits
    /// from facts already on record, never invent new biography or history.
    /// </remarks>
    public bool CastVoiceEnrichmentEnabled { get; set; }

    /// <summary>
    /// Path to the cast voice enrichment directive file, relative to
    /// <see cref="AgentProfileOptions.BasePath"/>. When the file does not exist, a built-in
    /// fallback directive is used.
    /// </summary>
    public string CastVoiceDirectivePath { get; set; } = "cast-voice-dream.md";

    /// <summary>
    /// Memory category the cast voice enrichment pass reads and writes. Has no default — the
    /// category name is deployment-specific, and the pass no-ops until one is configured.
    /// </summary>
    public string CastVoiceCategory { get; set; } = string.Empty;

    /// <summary>
    /// Marker text identifying an entry that already carries a voice card. A character with any
    /// entry containing this marker is skipped, which is what makes the pass converge instead of
    /// re-enriching the same cast every cycle.
    /// </summary>
    public string CastVoiceMarker { get; set; } = "VOICE CARD";

    /// <summary>
    /// Maximum number of characters enriched per dream cycle, so one cycle cannot rewrite an
    /// entire cast corpus. Default: 12.
    /// </summary>
    public int CastVoiceMaxPerCycle { get; set; } = 12;

    /// <summary>
    /// Whether cast voice enrichment requires conversation activity since the last cycle.
    /// Default: <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this the pass is gated only on "some character still lacks a voice card", which
    /// stays true for months. On an idle agent it is then the one pass that keeps spending: it
    /// ships the whole cast corpus on every cycle to invent voices for characters nobody has
    /// played with since the last time it ran. Voices are worth writing for the cast that just
    /// walked on stage, not for a corpus sitting untouched.
    /// </para>
    /// <para>
    /// The pass runs before preference inference clears the conversation log, so an empty log at
    /// that point means nothing happened this period. When no <see cref="IConversationLog"/> is
    /// registered the gate cannot be evaluated and the pass runs as before.
    /// </para>
    /// </remarks>
    public bool CastVoiceRequiresRecentActivity { get; set; } = true;

    /// <summary>
    /// Substrings that mark a voice card as written in the current format. A character whose card
    /// contains <see cref="CastVoiceMarker"/> but none of these is treated as an older card and is
    /// offered back to the pass for an upgrade. Empty (the default) means any card counts as
    /// finished, which is the original behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deployment that evolves what a voice card should contain otherwise has no way to bring
    /// the cards it already wrote up to the new shape: the pass skipped anything carrying the
    /// marker, so a directive asking the model to upgrade older cards produced proposals the
    /// framework silently discarded. Listing one or more markers of the current format closes
    /// that gap without the framework knowing anything about a particular card layout.
    /// </para>
    /// <para>
    /// Matching is case-insensitive, and only one of the listed markers has to be present — card
    /// sections are usually optional individually, so requiring all of them would treat a
    /// legitimately short card as stale and rewrite it forever.
    /// </para>
    /// </remarks>
    public IList<string> CastVoiceUpgradeMarkers { get; set; } = [];

    /// <summary>Whether the tier routing self-correction review pass is enabled.</summary>
    public bool TierRoutingReviewEnabled { get; set; } = true;

    /// <summary>
    /// How far back the tier routing review pass reads its routing log. Default: 14 days.
    /// Set to <see cref="TimeSpan.Zero"/> or negative to read the whole log.
    /// </summary>
    /// <remarks>
    /// The routing log is append-only and the pass reads the tail of it, so without a window an
    /// agent that stopped making routing decisions still had the same trailing entries analyzed
    /// on every cycle indefinitely. A window lets the input drain: once the newest entry ages
    /// out, the pass falls below its minimum-entry threshold and stops on its own.
    /// </remarks>
    public TimeSpan TierRoutingReviewWindow { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Routing cost floor for the tier-routing review pass. The High tier is priced at no less
    /// than this multiple of the Balanced tier's real rate when projecting threshold-scan and
    /// flagged-cluster costs. This stops the dream from reading a Balanced→High shift as
    /// zero-cost — and ratcheting <c>balancedCeiling</c> to its floor — when High currently
    /// shares Balanced's model. A genuinely premium High model keeps its real (higher) price.
    /// Set to 1.0 to disable the floor. Default: 2.0.
    /// </summary>
    public double TierRoutingHighCostFloorMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Target ceiling on the High-tier routing share (percent of routing decisions in the review
    /// window). While the observed High share exceeds this, the review pass will NOT apply a
    /// <c>balancedCeiling</c> decrease (which would push even more traffic to High) — a
    /// deterministic backstop independent of the LLM's judgment. Default: 20 (%).
    /// </summary>
    public double TierRoutingHighTargetPct { get; set; } = 20.0;

    /// <summary>
    /// Path to the routing dream directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string TierRoutingDirectivePath { get; set; } = "routing-dream.md";

    /// <summary>Whether the tool-call sequence skill detection pass is enabled.</summary>
    public bool SequenceSkillDetectionEnabled { get; set; } = true;

    /// <summary>
    /// Path to the sequence skill detection directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string SequenceSkillDirectivePath { get; set; } = "sequence-skill.md";

    /// <summary>Whether the entity extraction pass (requires <see cref="IKnowledgeGraph"/>) is enabled.</summary>
    public bool EntityExtractionEnabled { get; set; } = true;

    /// <summary>
    /// Path to the entity extraction directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string EntityExtractionDirectivePath { get; set; } = "entity-dream.md";

    /// <summary>Whether the graph consolidation pass (requires <see cref="IKnowledgeGraph"/>) is enabled.</summary>
    public bool GraphConsolidationEnabled { get; set; } = true;

    /// <summary>
    /// Path to the graph consolidation directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string GraphConsolidationDirectivePath { get; set; } = "graph-consolidation-dream.md";

    /// <summary>Whether the narrative identity reflection pass is enabled.</summary>
    public bool IdentityReflectionEnabled { get; set; } = true;

    /// <summary>
    /// Path to the identity reflection directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string IdentityDirectivePath { get; set; } = "identity-dream.md";

    /// <summary>
    /// Whether the dead-letter queue review pass is enabled.
    /// Requires <c>RabbitMq:ManagementApiBaseUrl</c> to be configured.
    /// </summary>
    public bool DlqReviewEnabled { get; set; } = true;

    /// <summary>
    /// Path to the DLQ review directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string DlqDirectivePath { get; set; } = "dlq-dream.md";

    /// <summary>Whether the wisp failure analysis pass (requires <see cref="IWispExecutionLog"/>) is enabled.</summary>
    public bool WispFailureAnalysisEnabled { get; set; } = true;

    /// <summary>
    /// Path to the wisp failure analysis directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string WispFailureDirectivePath { get; set; } = "wisp-failure-dream.md";

    /// <summary>
    /// Whether the wisp success analysis pass (requires <see cref="IWispExecutionLog"/>) is enabled.
    /// Detects wisp definitions that have repeated successfully across distinct sessions and
    /// promotes them to validated skill resources via <c>ISkillStore.AttachResourceAsync</c>.
    /// Symmetric complement to the failure pass.
    /// </summary>
    public bool WispSuccessAnalysisEnabled { get; set; } = true;

    /// <summary>
    /// Path to the wisp success analysis directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string WispSuccessDirectivePath { get; set; } = "wisp-success-dream.md";

    /// <summary>
    /// Minimum number of successful executions of the same definition hash required
    /// before the success pass considers it a candidate for promotion. Tighter than
    /// the failure pass (which uses <c>frequency &gt;= 3</c>) so we filter zero false
    /// positives over recall.
    /// </summary>
    public int WispSuccessFrequencyThreshold { get; set; } = 3;

    /// <summary>
    /// Whether the provisional skill-resource validation/demotion pass is enabled.
    /// Reads recent wisp records and resource checkouts, then flips provisional
    /// resources to non-provisional after repeated success or removes them after
    /// repeated failure. Requires <see cref="ISkillStore"/> and
    /// <see cref="IWispExecutionLog"/>; uses <see cref="IFailureClusterStore"/>
    /// to record demotions when present.
    /// </summary>
    public bool ProvisionalValidationEnabled { get; set; } = true;

    /// <summary>
    /// Distinct-session successful executions required to flip a provisional
    /// wisp resource to non-provisional. (Or distinct-session checkouts for
    /// non-wisp resources, which use access as a soft signal.)
    /// </summary>
    public int ProvisionalSuccessThreshold { get; set; } = 3;

    /// <summary>
    /// Number of consecutive recent failures of a provisional wisp resource that
    /// triggers removal of the resource and a corresponding entry in the
    /// failure-cluster store.
    /// </summary>
    public int ProvisionalFailureThreshold { get; set; } = 2;

    /// <summary>
    /// Provisional resources older than this with zero usage activity have a
    /// <c>[stale]</c> prefix added to their description so the LLM stops loading
    /// them. The body is preserved on disk in case it becomes interesting again.
    /// </summary>
    public TimeSpan ProvisionalStaleAfter { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Whether the tool-success-learning pass (requires <see cref="IToolCallLog"/>) is enabled.
    /// Mines the tool-call log for retry-until-success patterns and writes the verified
    /// argument values as durable long-term memory entries.
    /// </summary>
    public bool ToolSuccessLearningEnabled { get; set; } = true;

    /// <summary>
    /// Path to the tool-success-learning directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string ToolSuccessLearningDirectivePath { get; set; } = "tool-success-learning.md";

    /// <summary>
    /// Whether the observation framework pass is enabled. When true, the
    /// dream cycle runs the observation pipeline (theory-of-self,
    /// theory-of-user, plus any other registered targets) between the
    /// memory-mining and preference-inference passes. The framework
    /// requires <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    /// and <see cref="ILongTermMemory"/> to be registered; if either is
    /// missing, agent startup fails.
    /// </summary>
    public bool ObservationEnabled { get; set; } = true;

    /// <summary>
    /// Whether the Phase 3 self-repair contradiction sweep pass is enabled.
    /// LLM-mediated backstop for <c>claim/capability/*</c> and <c>feedback/*</c>
    /// contradictions the hot-path keyword detector missed.
    /// </summary>
    public bool ContradictionSweepEnabled { get; set; } = true;

    /// <summary>
    /// Path to the contradiction sweep directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string ContradictionSweepDirectivePath { get; set; } = "contradiction-sweep.md";

    /// <summary>
    /// Days of no reinforcement (measured against <see cref="MemoryEntry.LastSeenAt"/>)
    /// before importance decay begins. Entries younger than this are left alone regardless
    /// of their score. Default: 30 days.
    /// </summary>
    public int ImportanceDecayGraceDays { get; set; } = 30;

    /// <summary>
    /// Half-life (in calendar days) of a memory entry's importance once the grace period
    /// has passed. Decay is multiplicative and measured in calendar time: each decay pass
    /// multiplies importance by <c>0.5^(elapsedDays / HalfLifeDays)</c> where
    /// <c>elapsedDays</c> is the calendar time since the entry was last touched. This
    /// composes correctly, so the decay curve is invariant to <see cref="CronSchedule"/>
    /// cadence — running dream twice a day, four times a day, or once a week produces
    /// the same calendar-time decay for a given half-life.
    /// <para>
    /// With the defaults (HalfLife=45, Grace=30, Floor=0.10), a core 0.95 memory reaches
    /// the floor in roughly <b>176 days (~6 months)</b>; a routine 0.50 memory in ~134 days;
    /// a minor 0.30 memory in ~101 days. Set to zero or negative to disable decay entirely.
    /// </para>
    /// </summary>
    public float ImportanceDecayHalfLifeDays { get; set; } = 45f;

    /// <summary>
    /// Minimum importance score. Decay will never drive an entry below this value.
    /// Entries at or below the floor are skipped by the decay pass entirely.
    /// Default: 0.10 — low enough to de-rank stale entries but non-zero so they remain
    /// discoverable via keyword search.
    /// </summary>
    public float ImportanceDecayFloor { get; set; } = 0.10f;

    /// <summary>
    /// Whether the log-retention pass runs each dream cycle. When enabled, the dream
    /// prunes the append-only JSONL logs (skill-usage, tool-call, feedback,
    /// skill-resource-usage, wisp-executions) so they don't grow without bound.
    /// Disable to retain every log line/file indefinitely. Default: true.
    /// </summary>
    public bool LogRetentionEnabled { get; set; } = true;

    /// <summary>
    /// Per-session JSONL log files (one <c>{sessionId}.jsonl</c> per session for the
    /// skill-usage, tool-call, and feedback logs) older than this — by last-write
    /// time — are deleted by the retention pass. Set to <see cref="TimeSpan.Zero"/>
    /// or negative to disable age-based pruning. Default: 30 days.
    /// </summary>
    public TimeSpan LogRetentionMaxFileAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Ceiling on the number of per-session JSONL files kept in each session-log
    /// directory. After age pruning, if more remain, the oldest are deleted until the
    /// count is within this cap. Set to zero or negative to disable count-based
    /// pruning. Default: 1000.
    /// </summary>
    public int LogRetentionMaxFilesPerDirectory { get; set; } = 1000;

    /// <summary>
    /// Ceiling on the number of lines retained in any single JSONL log file. Applies to
    /// the single-file append-only logs (skill-resource-usage.jsonl,
    /// wisp-executions.jsonl) and to each individual per-session file (e.g. a persistent
    /// UI/CLI session's <c>{sessionId}.jsonl</c> that age/count pruning never reaps
    /// because it is continuously written). When a file exceeds this, the retention pass
    /// rewrites it keeping only the most recent lines. Set to zero or negative to disable
    /// trimming. Default: 50,000.
    /// </summary>
    public int LogRetentionMaxLinesPerFile { get; set; } = 50_000;
}
