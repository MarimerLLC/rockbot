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
public sealed record ScheduledTask(
    string Name,
    string CronExpression,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastFiredAt = null,
    bool RunOnce = false,
    bool IsSystemTask = false,
    string? Directive = null,
    ClientCapabilities ClientCapabilities = ClientCapabilities.None);
