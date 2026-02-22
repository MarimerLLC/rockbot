namespace RockBot.A2A;

/// <summary>
/// Capability advertisement for an agent.
/// Published to "discovery.announce" on startup.
/// </summary>
public sealed record AgentCard
{
    public required string AgentName { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<AgentSkill>? Skills { get; init; }

    /// <summary>
    /// When true, the agent is shutting down and should be removed from the directory.
    /// Published by <c>AgentDiscoveryService.StopAsync</c> on graceful shutdown.
    /// </summary>
    public bool IsDeregistering { get; init; }
}
