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

    /// <summary>
    /// Path to the file where per-caller trust entries are persisted.
    /// Relative paths are resolved from <see cref="AppContext.BaseDirectory"/>.
    /// Set to null or empty to disable persistence.
    /// </summary>
    public string? TrustStorePath { get; set; } = "agent-trust.json";

    /// <summary>
    /// Statically-configured agents that are always included in <c>list_known_agents</c>
    /// regardless of whether they have announced themselves on the discovery bus.
    /// Useful for ephemeral/KEDA agents that only run when invoked and therefore
    /// may not be present in the directory at query time.
    /// </summary>
    public List<AgentCard> WellKnownAgents { get; set; } = [];

    /// <summary>
    /// How often to re-fetch well-known peers' <c>/.well-known/agent-card.json</c>
    /// so changes to a peer's skills/metadata become visible without restarting.
    /// <see cref="TimeSpan.Zero"/> disables the periodic refresh. Default: 4 hours.
    /// </summary>
    public TimeSpan WellKnownRefreshInterval { get; set; } = TimeSpan.FromHours(4);

    // ── Long-running task polling ───────────────────────────────────────────

    /// <summary>Initial polling delay for long-running HTTP tasks (exponential backoff start).</summary>
    public TimeSpan PollingInitialDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum polling delay (exponential backoff cap).</summary>
    public TimeSpan PollingMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    // ── InputRequired multi-turn ────────────────────────────────────────────

    /// <summary>Maximum number of InputRequired round-trips before aborting the task.</summary>
    public int MaxInputRequiredRounds { get; set; } = 20;

    /// <summary>
    /// Number of consecutive identical question/answer repetitions before
    /// breaking the InputRequired loop. Modeled after the tool-call repetition
    /// detector in <c>AgentLoopRunner</c>.
    /// </summary>
    public int InputRequiredRepetitionThreshold { get; set; } = 3;
}
