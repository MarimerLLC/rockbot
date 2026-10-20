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

    /// <summary>
    /// How many turns evicted past <see cref="MaxTurnsPerSession"/> are kept in the session's
    /// archive (#509), so out-of-window recall can still reach them. The archive lasts as long as
    /// the live session: it is discarded when the session is cleared or expires. Set to 0 to
    /// discard evicted turns. Defaults to 500.
    /// </summary>
    public int MaxArchivedTurnsPerSession { get; set; } = 500;

    /// <summary>
    /// Directory for archived turns, resolved like <see cref="BasePath"/>. Defaults to
    /// <c>"conversations-archive"</c>; turns are stored as <c>{ArchivePath}/{sessionId}.jsonl</c>.
    /// Kept apart from <see cref="BasePath"/> so an archive is never mistaken for a session file.
    /// </summary>
    public string ArchivePath { get; set; } = "conversations-archive";
}
