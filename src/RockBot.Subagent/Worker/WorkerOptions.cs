namespace RockBot.Subagent.Worker;

/// <summary>
/// Configuration options for the worker subagent subsystem (the lean rung between
/// wisps and subagents). See <c>design/worker-subagents.md</c> for rationale.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>
    /// Maximum number of workers to execute concurrently within a single batch.
    /// Mirrors <see cref="SubagentOptions.MaxConcurrentSubagents"/> initial default;
    /// tune once production data is available.
    /// </summary>
    public int MaxConcurrentWorkers { get; set; } = 3;

    /// <summary>
    /// Default wall-clock cap for a single worker when the caller does not supply
    /// one. Tighter than subagents because workers are leaf gather tasks.
    /// </summary>
    public int DefaultTimeoutMinutes { get; set; } = 5;

    /// <summary>
    /// Default tool-calling iteration cap. Workers are mechanical and should not
    /// need many turns; failure to converge under the cap is treated as a hard
    /// failure rather than an extended deliberation.
    /// </summary>
    public int DefaultMaxIterations { get; set; } = 12;
}
