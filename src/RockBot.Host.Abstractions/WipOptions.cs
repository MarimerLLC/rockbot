namespace RockBot.Host;

/// <summary>
/// Configuration for the work-in-progress tracker.
/// </summary>
public sealed class WipOptions
{
    /// <summary>
    /// Base directory for WIP entry files.
    /// Defaults to <c>"wip"</c>, resolved under <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string BasePath { get; set; } = "wip";

    /// <summary>
    /// Maximum age of a WIP entry before it is considered stale and abandoned
    /// on recovery. Defaults to 30 minutes.
    /// </summary>
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromMinutes(30);
}
