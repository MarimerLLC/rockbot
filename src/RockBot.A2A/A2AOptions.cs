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
}
