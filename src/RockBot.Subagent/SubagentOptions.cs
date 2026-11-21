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
    /// How long the consolidation gate keeps waiting for a sibling that has finished but whose
    /// result has not reached the gate yet (#858). The result crosses the broker and the result
    /// handler's per-result work (which can include a decision-model call) after the subagent
    /// leaves the active set. Once this passes, the batch stops waiting for it; a result that
    /// arrives later is dropped as a late arrival. Capped by the consolidation ceiling.
    /// </summary>
    public int ResultArrivalGraceSeconds { get; set; } = 60;

    /// <summary>
    /// Characters of full text inlined into a new subagent's prior-work block (#665): the spawn
    /// <c>inputs</c> the primary passed, then any relevant earlier result that saved no keys. Earlier
    /// results with saved keys are listed as summary and keys for the subagent to fetch (#695) — the
    /// block is re-sent on every iteration of the subagent's loop, so inlined text is paid for each
    /// time. 0 disables inlining; the pointer list is still injected. Default 8,000.
    /// </summary>
    public int LineageInlineBudgetChars { get; set; } = 8_000;

    /// <summary>
    /// Minimum <c>max_iterations</c> for a subagent whose description asks it to research, verify,
    /// synthesize, outline or draft (#665). A lower requested cap is raised to this and logged.
    /// 0 disables the floor.
    /// </summary>
    public int ResearchIterationFloor { get; set; } = 20;
}
