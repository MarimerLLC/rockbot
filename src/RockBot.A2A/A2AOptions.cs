namespace RockBot.A2A;

/// <summary>
/// Configuration options for the A2A protocol layer.
/// </summary>
public sealed class A2AOptions
{
    public string DefaultResultTopic { get; set; } = "agent.response";
    public string StatusTopic { get; set; } = "agent.task.status";
    public string TaskTopic { get; set; } = "agent.task";
    public string CancelTopic { get; set; } = "agent.task.cancel";
    public string DiscoveryTopic { get; set; } = "discovery.announce";
    public AgentCard? Card { get; set; }

    /// <summary>Topic prefix where this agent receives A2A task results and errors.
    /// The full per-agent topic is "{CallerResultTopic}.{agentName}".</summary>
    public string CallerResultTopic { get; set; } = "agent.response";
}
