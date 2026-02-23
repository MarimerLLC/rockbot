namespace RockBot.Host;

/// <summary>
/// Configuration for cross-session shared memory.
/// </summary>
public sealed class SharedMemoryOptions
{
    /// <summary>Default TTL when callers do not specify one. Defaults to 30 minutes.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum number of live entries.
    /// New entries are rejected (with a warning) once this limit is reached.
    /// Defaults to 100.
    /// </summary>
    public int MaxEntries { get; set; } = 100;

    /// <summary>
    /// Base directory for persisting shared memory to disk.
    /// Defaults to <c>"shared-memory"</c>, resolved under <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string BasePath { get; set; } = "shared-memory";
}
