namespace RockBot.Subagent;

/// <summary>
/// Tracks a running subagent task.
/// </summary>
public sealed class SubagentEntry
{
    public required string TaskId { get; init; }
    public required string SubagentSessionId { get; init; }
    public required string PrimarySessionId { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required CancellationTokenSource CancellationTokenSource { get; init; }
    public required Task Task { get; init; }
    public string? BatchId { get; init; }
    public bool Consolidate { get; init; } = true;

    /// <summary>
    /// The user message that led the primary agent to spawn this subagent, when known (#666).
    /// Carried to the result so the synthesis turn is checked against what the user asked.
    /// </summary>
    public string? OriginatingUserRequest { get; init; }

    /// <summary>
    /// The consequential-action scope of the run that spawned this subagent (#685), captured at
    /// spawn. The subagent's own run inherits it, so a subagent spawned from an information-only
    /// user message cannot make the external changes that message didn't ask for.
    /// </summary>
    public RockBot.Host.ActionGateScope? ActionGate { get; init; }
}
