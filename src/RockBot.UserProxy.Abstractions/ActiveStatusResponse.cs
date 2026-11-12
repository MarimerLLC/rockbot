namespace RockBot.UserProxy;

/// <summary>
/// Snapshot of currently active background work returned in response to
/// <see cref="ActiveStatusRequest"/>. The UI uses this to reconcile stale
/// indicators (spinners, header badges) on startup or reconnect. The agent also
/// broadcasts an empty one on <see cref="UserProxyTopics.UserEvent"/> when it starts,
/// so a UI left showing work from before a restart clears it.
/// </summary>
public sealed record ActiveStatusResponse
{
    /// <summary>Running subagents only. Kept for older proxies; newer ones read <see cref="Work"/>.</summary>
    public required IReadOnlyList<ActiveSubagentInfo> Subagents { get; init; }
    public required bool IsProcessing { get; init; }

    /// <summary>All running background work: subagents and workers. Null from older agents.</summary>
    public IReadOnlyList<ActiveWorkItem>? Work { get; init; }
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

/// <summary>
/// One piece of running background work: a subagent or a worker.
/// </summary>
public sealed record ActiveWorkItem
{
    public required string TaskId { get; init; }

    /// <summary><see cref="ReplySourceKind.Subagent"/> or <see cref="ReplySourceKind.Worker"/>.</summary>
    public required string Kind { get; init; }

    public required string Description { get; init; }

    /// <summary>The raw session id of the primary session that spawned it.</summary>
    public string? ThreadId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public string? ParentToolCallId { get; init; }
}
