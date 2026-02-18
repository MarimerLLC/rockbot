namespace RockBot.Host;

/// <summary>
/// Configuration for the file-based skill store.
/// </summary>
public sealed class SkillOptions
{
    /// <summary>
    /// Path to the skills directory, relative to the agent profile base path.
    /// Defaults to <c>"skills"</c>.
    /// </summary>
    public string BasePath { get; set; } = "skills";
}
