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
    /// Optional base URL for HTTP-transport agents (e.g. "http://localhost:5100").
    /// When set, <c>invoke_agent</c> dispatches tasks via HTTP POST to
    /// <c>{Url}/tasks/send</c> instead of the RabbitMQ message bus.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// When true, the agent is shutting down and should be removed from the directory.
    /// Published by <c>AgentDiscoveryService.StopAsync</c> on graceful shutdown.
    /// </summary>
    public bool IsDeregistering { get; init; }
}
