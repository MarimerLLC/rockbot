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
}
