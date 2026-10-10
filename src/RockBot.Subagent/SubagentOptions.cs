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

    /// <summary>
    /// Characters of prior work (spawn <c>inputs</c> first, then the most relevant earlier subagent
    /// results) inlined in full into a new subagent's context (#665). Everything else is listed as
    /// a pointer the subagent can fetch. 0 disables inlining; the pointer list is still injected.
    /// </summary>
    public int LineageInlineBudgetChars { get; set; } = 24_000;

    /// <summary>
    /// Minimum <c>max_iterations</c> for a subagent whose description asks it to research, verify,
    /// synthesize, outline or draft (#665). A lower requested cap is raised to this and logged.
    /// 0 disables the floor.
    /// </summary>
    public int ResearchIterationFloor { get; set; } = 20;
}
