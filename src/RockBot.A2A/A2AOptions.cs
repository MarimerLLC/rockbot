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

    /// <summary>
    /// Path to the file where the agent directory is persisted across restarts.
    /// Relative paths are resolved from <see cref="AppContext.BaseDirectory"/>.
    /// Set to null or empty to disable persistence.
    /// </summary>
    public string DirectoryPersistencePath { get; set; } = "known-agents.json";

    /// <summary>
    /// How long a directory entry is kept after its last announcement.
    /// Entries older than this are pruned on startup. Default: 24 hours.
    /// </summary>
    public TimeSpan DirectoryEntryTtl { get; set; } = TimeSpan.FromHours(24);
}
