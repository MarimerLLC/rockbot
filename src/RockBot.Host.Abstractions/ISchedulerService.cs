namespace RockBot.Host;

/// <summary>
/// Manages the lifecycle of scheduled tasks at runtime. Implemented by the scheduler
/// hosted service; used directly by tool executors.
/// </summary>
public interface ISchedulerService
{
    /// <summary>
    /// Schedules a new task (or replaces an existing one with the same name).
    /// Persists to the store and arms a timer immediately.
    /// </summary>
    Task ScheduleAsync(ScheduledTask task, CancellationToken ct = default);

    /// <summary>
    /// Cancels and removes a scheduled task by name.
    /// Returns true if the task existed, false if not found.
    /// </summary>
    Task<bool> CancelAsync(string name, CancellationToken ct = default);

    /// <summary>Returns all currently scheduled tasks.</summary>
    Task<IReadOnlyList<ScheduledTask>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the next time the given task will fire (in the agent's configured timezone),
    /// or null if the cron expression has no future occurrence.
    /// </summary>
    DateTimeOffset? GetNextOccurrence(ScheduledTask task);
}
