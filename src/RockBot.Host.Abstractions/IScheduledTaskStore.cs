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

    /// <summary>
    /// Replaces an exact piece of text inside an existing task's
    /// <see cref="ScheduledTask.Directive"/>, leaving the rest of it and every other field
    /// untouched.
    /// </summary>
    /// <param name="name">Task whose directive to edit.</param>
    /// <param name="oldText">Exact text to find. Must be non-empty.</param>
    /// <param name="newText">Replacement text. May be empty to delete the match.</param>
    /// <param name="replaceAll">
    /// When <c>true</c>, replaces every occurrence. When <c>false</c>, more than one
    /// occurrence is refused rather than guessed at.
    /// </param>
    /// <remarks>
    /// A directive is a checklist a recurring task accumulates over many fires, and
    /// <see cref="UpdateDirectiveAsync"/> replaces it wholesale — so a task that wants to add
    /// one line has to restate every line it wants to keep. Unlike that method, an unknown
    /// task name is reported rather than silently ignored: the caller asked to change specific
    /// text and needs to know it did not happen.
    /// </remarks>
    Task<ContentEditResult> EditDirectiveAsync(string name, string oldText, string newText, bool replaceAll = false)
        => Task.FromResult(ContentEditResult.NotSupported);
}
