namespace RockBot.Host;

/// <summary>
/// A named task that fires on a cron schedule and dispatches through the agent's LLM pipeline.
/// </summary>
/// <param name="Name">Unique name identifying the task.</param>
/// <param name="CronExpression">Standard 5-field cron expression (e.g. "0 8 * * 1-5").</param>
/// <param name="Description">What the agent should do when this task fires.</param>
/// <param name="CreatedAt">When the task was first scheduled.</param>
/// <param name="LastFiredAt">When the task most recently fired, or null if never.</param>
public sealed record ScheduledTask(
    string Name,
    string CronExpression,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastFiredAt = null);
