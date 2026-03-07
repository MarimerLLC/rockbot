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

    /// <summary>Whether the tier routing self-correction review pass is enabled.</summary>
    public bool TierRoutingReviewEnabled { get; set; } = true;

    /// <summary>
    /// Path to the routing dream directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// When the file does not exist, a built-in fallback directive is used.
    /// </summary>
    public string TierRoutingDirectivePath { get; set; } = "routing-dream.md";
}
