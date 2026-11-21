using System.Text.Json.Serialization;
using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// A named task that fires on a cron schedule and dispatches through the agent's LLM pipeline.
/// </summary>
/// <param name="Name">Unique name identifying the task.</param>
/// <param name="CronExpression">Standard 5-field cron expression (e.g. "0 8 * * 1-5").</param>
/// <param name="Description">What the agent should do when this task fires.</param>
/// <param name="CreatedAt">When the task was first scheduled.</param>
/// <param name="LastFiredAt">When the task most recently fired, or null if never.</param>
/// <param name="RunOnce">
/// When true the task is automatically cancelled after it fires once.
/// Use this for one-time reminders and deferred actions.
/// </param>
/// <param name="IsSystemTask">
/// When true the task is a system-internal background task (e.g. heartbeat patrol)
/// whose results are collapsed in the UI. User-created tasks default to false.
/// </param>
/// <param name="Directive">
/// Optional, evolving free-form directive content the agent maintains for itself across runs of
/// this task (e.g. the heartbeat patrol's running checklist). When non-null the content is
/// injected as a system message immediately after the task's static framing on every fire,
/// and the agent updates it via the <c>update_task_directive</c> tool. Distinct from
/// <see cref="Description"/>, which is the user-facing prompt the task fires with.
/// </param>
/// <param name="ClientCapabilities">
/// Rendering capabilities the task's output should be authored for. Persists with the schedule
/// so every fire produces the same rich-content subset regardless of who is currently connected
/// — author-time intent rather than runtime audience. Defaults to <see cref="UserProxy.ClientCapabilities.None"/>
/// (markdown-only). Scheduled tasks deliberately ignore the live-session capability stash
/// because their audience is unpredictable at fire time.
/// </param>
/// <param name="TimeZone">
/// Which zone the cron fields are written in: <c>"home"</c> (or null) for the user's home zone,
/// <c>"local"</c> to follow the user's current zone as they travel, or a fixed IANA id. Null keeps
/// the behavior of tasks scheduled before per-task zones existed.
/// </param>
/// <param name="MaxTier">
/// The highest model tier this task's run may use: its own patrol turn (normally Balanced) and
/// every subagent it spawns. Null leaves routing alone. Set by the dream's scheduled-task tier
/// pass when the task's run history shows High runs costing far more than Balanced runs that
/// did the same job (#846).
/// </param>
/// <param name="MaxTierReason">Why <paramref name="MaxTier"/> was set, for the logs and the Usage tab.</param>
/// <param name="Gate">
/// A saved wisp run before the LLM on every fire (#848). When it finds nothing new the LLM turn
/// is skipped, so a recurring "nothing new" sweep costs almost nothing. Null runs the task as usual.
/// </param>
public sealed record ScheduledTask(
    string Name,
    string CronExpression,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastFiredAt = null,
    bool RunOnce = false,
    bool IsSystemTask = false,
    string? Directive = null,
    ClientCapabilities ClientCapabilities = ClientCapabilities.None,
    string? TimeZone = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ModelTier? MaxTier = null,
    string? MaxTierReason = null,
    TaskGate? Gate = null)
{
    /// <summary><paramref name="tier"/>, lowered to <see cref="MaxTier"/> when this task sets one.</summary>
    public ModelTier Cap(ModelTier tier) =>
        MaxTier is { } max && tier > max ? max : tier;
}

/// <summary>When a <see cref="TaskGate"/> lets the LLM turn run.</summary>
public enum TaskGateMode
{
    /// <summary>Run when the wisp's output holds any results.</summary>
    NonEmpty,

    /// <summary>Run when the wisp's output holds results that differ from the last run's.</summary>
    Changed,
}

/// <summary>
/// A saved wisp a scheduled task runs deterministically before its LLM turn (#848). A saved wisp is
/// a skill resource, so it is named by skill and resource file. The wisp should fetch only what is new
/// since the last run; it may use <c>{{last_run_at}}</c> (ISO 8601) and <c>{{last_run_date}}</c>
/// (<c>yyyy/MM/dd</c>), filled from the task's last fire.
/// </summary>
/// <param name="Skill">The skill the wisp is attached to.</param>
/// <param name="Resource">The wisp's resource file name on that skill.</param>
/// <param name="Mode">When the LLM turn runs.</param>
/// <param name="MaxConsecutiveSkips">
/// Skipped runs in a row after which the task runs anyway, so a gate that is wrong cannot silence
/// the task for good. Zero or less never forces a run.
/// </param>
/// <param name="EnabledBy">Who switched the gate on: <c>user</c> or <c>optimizer</c>.</param>
/// <param name="Reason">Why it was switched on.</param>
/// <param name="LastResultHash">A hash of the wisp's last non-empty output, for <see cref="TaskGateMode.Changed"/>.</param>
/// <param name="ConsecutiveSkips">Runs skipped in a row so far.</param>
public sealed record TaskGate(
    string Skill,
    string Resource,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] TaskGateMode Mode = TaskGateMode.NonEmpty,
    int MaxConsecutiveSkips = TaskGate.DefaultMaxConsecutiveSkips,
    string? EnabledBy = null,
    string? Reason = null,
    string? LastResultHash = null,
    int ConsecutiveSkips = 0)
{
    /// <summary>The default for <see cref="MaxConsecutiveSkips"/>.</summary>
    public const int DefaultMaxConsecutiveSkips = 6;

    /// <summary>The wisp as <c>skill/resource</c>, for display.</summary>
    [JsonIgnore]
    public string Target => $"{Skill}/{Resource}";
}
