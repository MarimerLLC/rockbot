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

    /// <summary>The id of the tool call that spawned this subagent, when known (#795).</summary>
    public string? ParentToolCallId { get; init; }

    /// <summary>Ensures exactly one <c>SubagentFinished</c> is published for this run (#795).</summary>
    internal SubagentFinishGuard FinishGuard { get; init; } = new();
}

/// <summary>A once-flag shared by the paths that can report a subagent finished.</summary>
internal sealed class SubagentFinishGuard
{
    private int _finished;

    /// <summary>True for the first caller only.</summary>
    public bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;
}

/// <summary>How a <see cref="SubagentRunner"/> run ended (#795).</summary>
/// <param name="IsSuccess">The run produced its output.</param>
/// <param name="FailureReason"><c>timeout</c>, <c>cancelled</c> or <c>exception</c> for a failed run.</param>
/// <param name="Error">What went wrong.</param>
public sealed record SubagentRunOutcome(bool IsSuccess, string? FailureReason = null, string? Error = null)
{
    /// <summary>The <see cref="RockBot.UserProxy.SubagentOutcome"/> this maps to.</summary>
    public string EventOutcome => IsSuccess
        ? RockBot.UserProxy.SubagentOutcome.Success
        : FailureReason switch
        {
            "timeout" => RockBot.UserProxy.SubagentOutcome.Timeout,
            "cancelled" => RockBot.UserProxy.SubagentOutcome.Cancelled,
            _ => RockBot.UserProxy.SubagentOutcome.Failed,
        };
}
