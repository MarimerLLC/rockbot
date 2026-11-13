namespace RockBot.UserProxy;

/// <summary>
/// A subagent or worker started. Broadcast on <see cref="UserProxyTopics.UserEvent"/>.
/// </summary>
public sealed record SubagentStarted
{
    /// <summary>The subagent's or worker's task id.</summary>
    public required string SubagentRunId { get; init; }

    /// <summary><see cref="ReplySourceKind.Subagent"/> or <see cref="ReplySourceKind.Worker"/>.</summary>
    public required string Kind { get; init; }

    public required string Description { get; init; }

    /// <summary>The raw session id of the primary session that spawned it.</summary>
    public required string ThreadId { get; init; }

    /// <summary>The id of the tool call that spawned it, when known.</summary>
    public string? ParentToolCallId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
}

/// <summary>
/// A subagent or worker finished. Broadcast on <see cref="UserProxyTopics.UserEvent"/>, once per run.
/// </summary>
public sealed record SubagentFinished
{
    public required string SubagentRunId { get; init; }

    /// <summary><see cref="ReplySourceKind.Subagent"/> or <see cref="ReplySourceKind.Worker"/>.</summary>
    public required string Kind { get; init; }

    public required string ThreadId { get; init; }

    /// <summary>One of <see cref="SubagentOutcome"/>.</summary>
    public required string Outcome { get; init; }

    public string? Error { get; init; }

    public required DateTimeOffset FinishedAt { get; init; }
}

/// <summary>Outcomes a <see cref="SubagentFinished"/> reports.</summary>
public static class SubagentOutcome
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Timeout = "timeout";
}
