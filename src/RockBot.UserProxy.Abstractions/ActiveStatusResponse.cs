namespace RockBot.UserProxy;

/// <summary>
/// Snapshot of currently active background work returned in response to
/// <see cref="ActiveStatusRequest"/>. The UI uses this to reconcile stale
/// indicators (spinners, header badges) on startup or reconnect.
/// </summary>
public sealed record ActiveStatusResponse
{
    public required IReadOnlyList<ActiveSubagentInfo> Subagents { get; init; }
    public required bool IsProcessing { get; init; }
}

/// <summary>
/// Describes a currently running subagent.
/// </summary>
public sealed record ActiveSubagentInfo
{
    public required string TaskId { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
}
