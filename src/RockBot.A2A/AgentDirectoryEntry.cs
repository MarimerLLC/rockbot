namespace RockBot.A2A;

/// <summary>
/// A known agent in the directory, with the timestamp of its most recent announcement.
/// </summary>
public sealed record AgentDirectoryEntry
{
    public required AgentCard Card { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>
    /// True for agents seeded from <see cref="A2AOptions.WellKnownAgents"/>.
    /// Well-known entries are never removed by deregistration announcements —
    /// the agent is always callable even when it is not currently running.
    /// </summary>
    public bool IsWellKnown { get; init; }
}
