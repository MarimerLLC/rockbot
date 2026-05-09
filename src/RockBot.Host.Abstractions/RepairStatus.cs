namespace RockBot.Host;

/// <summary>
/// Lifecycle state of a <see cref="RepairTicket"/>. See <c>design/self-repair.md</c> Phase 4.
/// </summary>
public enum RepairStatus
{
    /// <summary>Created, no apply attempted yet (or pending another retry after a failed verify).</summary>
    Open,

    /// <summary>Apply has started for the current cycle; will transition to Resolved/Open/Escalated when the cycle finishes.</summary>
    InProgress,

    /// <summary>Verify succeeded after apply — the change is considered to have fixed the cluster.</summary>
    Resolved,

    /// <summary>Apply attempts exhausted (default 3) without a successful verify; surfaced via <c>repair-escalations-latest</c>.</summary>
    Escalated,
}
