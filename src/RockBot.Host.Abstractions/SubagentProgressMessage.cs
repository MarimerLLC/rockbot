namespace RockBot.Host;

/// <summary>
/// Published by a running subagent to report progress back to the primary session.
/// </summary>
public sealed record SubagentProgressMessage
{
    public required string TaskId { get; init; }
    public required string SubagentSessionId { get; init; }
    public required string PrimarySessionId { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
