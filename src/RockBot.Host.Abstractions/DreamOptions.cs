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

    /// <summary>Whether the memory mining pass (requires <see cref="IConversationLog"/>) is enabled.</summary>
    public bool MemoryMiningEnabled { get; set; } = true;

    /// <summary>
    /// Path to the memory mining directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string MemoryMiningDirectivePath { get; set; } = "memory-mining.md";

    /// <summary>Whether the tier routing self-correction review pass is enabled.</summary>
    public bool TierRoutingReviewEnabled { get; set; } = true;

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

    /// <summary>
    /// Skill name prefixes that are protected from deletion by dream consolidation and
    /// optimization passes. Skills whose names start with any of these prefixes (case-insensitive)
    /// will never be deleted, even if the LLM proposes merging or removing them.
    /// Default: <c>["patrol/"]</c> — protects the patrol checklist skills that are referenced
    /// by system directives.
    /// </summary>
    public List<string> ProtectedSkillPrefixes { get; set; } = ["patrol/"];

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
}
