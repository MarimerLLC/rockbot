namespace RockBot.Subagent;

/// <summary>
/// Manages the lifecycle of subagent tasks.
/// </summary>
public interface ISubagentManager
{
    /// <summary>
    /// Spawns a new subagent. Returns the task ID immediately (fire-and-forget).
    /// Returns an error message string if the concurrency limit is reached.
    /// <paramref name="originatingUserRequest"/> is the user message that led to the spawn (#666);
    /// it travels with the task to the result so the synthesis turn can be checked against it.
    /// </summary>
    Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
        string primarySessionId, CancellationToken ct,
        string? batchId = null, bool consolidate = true, int? maxIterations = null,
        string? originatingUserRequest = null);

    /// <summary>
    /// Cancels a running subagent by task ID. Returns true if found and cancelled.
    /// </summary>
    Task<bool> CancelAsync(string taskId);

    /// <summary>
    /// Lists currently active (running) subagent entries.
    /// </summary>
    IReadOnlyList<SubagentEntry> ListActive();
}
