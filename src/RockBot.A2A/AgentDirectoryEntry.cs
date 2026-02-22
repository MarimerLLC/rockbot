namespace RockBot.A2A;

/// <summary>
/// A known agent in the directory, with the timestamp of its most recent announcement.
/// </summary>
public sealed record AgentDirectoryEntry
{
    public required AgentCard Card { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }
}
