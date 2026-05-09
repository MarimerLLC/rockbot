namespace RockBot.Host;

/// <summary>
/// Persistent store for scheduled tasks.
/// </summary>
public interface IScheduledTaskStore
{
    /// <summary>Creates or replaces a scheduled task.</summary>
    Task SaveAsync(ScheduledTask task);

    /// <summary>Returns the task by name, or null if not found.</summary>
    Task<ScheduledTask?> GetAsync(string name);

    /// <summary>Returns all scheduled tasks ordered by name.</summary>
    Task<IReadOnlyList<ScheduledTask>> ListAsync();

    /// <summary>Removes a task. Returns true if found and deleted, false if not found.</summary>
    Task<bool> DeleteAsync(string name);

    /// <summary>Updates the LastFiredAt timestamp for an existing task. No-op if not found.</summary>
    Task UpdateLastFiredAsync(string name, DateTimeOffset firedAt);

    /// <summary>
    /// Replaces the <see cref="ScheduledTask.Directive"/> body for an existing task.
    /// No-op if the task does not exist. Other fields (cron, description, timestamps,
    /// system/runOnce flags) are left unchanged.
    /// </summary>
    Task UpdateDirectiveAsync(string name, string directive);
}
