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
    /// Days of no reinforcement (measured against <see cref="MemoryEntry.LastSeenAt"/>)
    /// before importance decay begins. Entries younger than this are left alone regardless
    /// of their score. Default: 30 days.
    /// </summary>
    public int ImportanceDecayGraceDays { get; set; } = 30;

    /// <summary>
    /// Half-life (in calendar days) of a memory entry's importance once the grace period
    /// has passed. Decay is multiplicative: an entry's importance is multiplied by
    /// <c>0.5^(1 / (HalfLifeDays · cycles-per-day))</c> each dream cycle, producing an
    /// exponential curve that drops quickly near full importance and slows as it
    /// approaches the floor.
    /// <para>
    /// With the defaults (HalfLife=45, Grace=30, Floor=0.10, default 12h cron → 2 cycles/day),
    /// a core 0.95 memory reaches the floor in roughly <b>176 days (~6 months)</b>; a
    /// routine 0.50 memory in ~134 days; a minor 0.30 memory in ~101 days.
    /// </para>
    /// <para>
    /// <b>Cron cadence assumption:</b> The factor is computed assuming 2 dream cycles per
    /// day (matches the default <c>0 */12 * * *</c>). If you change <see cref="CronSchedule"/>
    /// to run more or less often, adjust <see cref="ImportanceDecayHalfLifeDays"/> accordingly
    /// to preserve the calendar-time shape — e.g. at an hourly cadence, multiply halflife by 12.
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
