namespace RockBot.Host;

/// <summary>
/// Options for conversation memory.
/// </summary>
public sealed class ConversationMemoryOptions
{
    /// <summary>
    /// Maximum number of turns to retain per session. Oldest turns are evicted when exceeded.
    /// Defaults to 50.
    /// </summary>
    public int MaxTurnsPerSession { get; set; } = 50;

    /// <summary>
    /// Duration of inactivity after which a session's turns are discarded.
    /// Defaults to 1 hour.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Base directory for persisting conversation sessions to disk.
    /// Defaults to <c>"conversations"</c>, resolved under <see cref="AgentProfileOptions.BasePath"/>.
    /// Set to an absolute path to override. Sessions are stored as <c>{BasePath}/{sessionId}.json</c>.
    /// </summary>
    public string BasePath { get; set; } = "conversations";
}
