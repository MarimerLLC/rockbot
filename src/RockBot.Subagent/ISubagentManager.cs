namespace RockBot.Subagent;

/// <summary>
/// Manages the lifecycle of subagent tasks.
/// </summary>
public interface ISubagentManager
{
    /// <summary>
    /// Spawns a new subagent. Returns the task ID immediately (fire-and-forget).
    /// Returns an error message string if the concurrency limit is reached.
    /// </summary>
    Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
        string primarySessionId, CancellationToken ct);

    /// <summary>
    /// Cancels a running subagent by task ID. Returns true if found and cancelled.
    /// </summary>
    Task<bool> CancelAsync(string taskId);

    /// <summary>
    /// Lists currently active (running) subagent entries.
    /// </summary>
    IReadOnlyList<SubagentEntry> ListActive();
}
