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

    /// <summary>
    /// Per-worker cap on how much of a worker's findings the <c>spawn_workers</c> receipt
    /// inlines for the spawning agent. Results at or under the cap are handed back in full,
    /// so consuming a batch costs no extra retrieval round-trip; anything larger is excerpted
    /// with an instruction to fetch the rest by <c>result_key</c>.
    /// </summary>
    /// <remarks>
    /// The cap is per worker, so a batch of three can add roughly three times this much to the
    /// spawning agent's context. Lower it if worker payloads start crowding out the request.
    /// Set to 0 or below to inline every result in full regardless of size.
    /// </remarks>
    public int MaxInlineResultChars { get; set; } = 4000;
}
