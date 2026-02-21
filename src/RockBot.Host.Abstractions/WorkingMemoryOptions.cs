namespace RockBot.Host;

/// <summary>
/// Configuration for session-scoped working memory.
/// </summary>
public sealed class WorkingMemoryOptions
{
    /// <summary>Default TTL when callers do not specify one. Defaults to 5 minutes.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of live entries per session.
    /// New entries are rejected (with a warning) once this limit is reached.
    /// Defaults to 20.
    /// </summary>
    public int MaxEntriesPerSession { get; set; } = 50;

    /// <summary>
    /// Base directory for persisting working memory sessions to disk.
    /// Defaults to <c>"working-memory"</c>, resolved under <see cref="AgentProfileOptions.BasePath"/>.
    /// Set to an absolute path to override. Sessions are stored as <c>{BasePath}/{sessionId}.json</c>.
    /// </summary>
    public string BasePath { get; set; } = "working-memory";
}
