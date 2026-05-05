namespace RockBot.Subagent;

/// <summary>
/// Configuration options for the subagent subsystem.
/// </summary>
public sealed class SubagentOptions
{
    public int MaxConcurrentSubagents { get; set; } = 3;
    public int DefaultTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Legacy ceiling for the consolidation gate. Kept for one release as a fallback
    /// when neither <see cref="BackgroundConsolidationTimeoutSeconds"/> nor
    /// <see cref="InteractiveConsolidationTimeoutSeconds"/> is set explicitly. Prefer
    /// the per-context fields below.
    /// </summary>
    public int ConsolidationTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum time the consolidation gate waits for sibling subagents to complete
    /// when the primary session is non-interactive (e.g. scheduled patrol). Stragglers
    /// still active at the ceiling are cancelled and surfaced as failures in the
    /// final synthesis. Conservative initial value — bumpable after observing whether
    /// the broker tolerates long handler holds.
    /// </summary>
    public int BackgroundConsolidationTimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum time the consolidation gate waits for sibling subagents to complete
    /// when the primary session is interactive (chat). Shorter than the background
    /// ceiling so an interactive user is not left waiting indefinitely.
    /// </summary>
    public int InteractiveConsolidationTimeoutSeconds { get; set; } = 300;
}
