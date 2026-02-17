namespace RockBot.Host;

/// <summary>
/// Configuration options for the agent host.
/// </summary>
public sealed class AgentHostOptions
{
    /// <summary>
    /// Topics the agent subscribes to.
    /// </summary>
    public List<string> Topics { get; } = [];
}
