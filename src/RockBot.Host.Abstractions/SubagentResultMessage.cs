namespace RockBot.Host;

/// <summary>
/// Published by a subagent when it has completed its task.
/// </summary>
public sealed record SubagentResultMessage
{
    public required string TaskId { get; init; }
    public required string SubagentSessionId { get; init; }
    public required string PrimarySessionId { get; init; }
    public required string Output { get; init; }
    public required bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? BatchId { get; init; }
    public bool Consolidate { get; init; } = true;

    /// <summary>
    /// The user message that led the primary agent to spawn this subagent, when known (#666).
    /// The synthesis turn's completion check judges the relayed result against it rather than
    /// against the subagent's own task description, which the primary may have paraphrased.
    /// </summary>
    public string? OriginatingUserRequest { get; init; }

    /// <summary>
    /// The task description the subagent was spawned with (#665). Recorded with the result in the
    /// session work registry so later subagents in the conversation can see what this one did.
    /// Null on results from an older build.
    /// </summary>
    public string? Description { get; init; }
}
