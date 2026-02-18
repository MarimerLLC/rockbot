namespace RockBot.Host;

/// <summary>
/// Options for in-memory conversation memory.
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
}
