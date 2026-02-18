namespace RockBot.Host;

/// <summary>
/// Options for locating agent profile documents on disk.
/// Relative paths are resolved against <c>IHostEnvironment.ContentRootPath</c>.
/// </summary>
public sealed class AgentProfileOptions
{
    /// <summary>
    /// Base directory for profile documents. Defaults to <c>"agent"</c>.
    /// </summary>
    public string BasePath { get; set; } = "agent";

    /// <summary>
    /// Path to the soul document. When relative, resolved under <see cref="BasePath"/>.
    /// Defaults to <c>"soul.md"</c>.
    /// </summary>
    public string SoulPath { get; set; } = "soul.md";

    /// <summary>
    /// Path to the directives document. When relative, resolved under <see cref="BasePath"/>.
    /// Defaults to <c>"directives.md"</c>.
    /// </summary>
    public string DirectivesPath { get; set; } = "directives.md";

    /// <summary>
    /// Path to the optional style document. When relative, resolved under <see cref="BasePath"/>.
    /// Null means no style document is expected.
    /// Defaults to <c>"style.md"</c>.
    /// </summary>
    public string? StylePath { get; set; } = "style.md";

    /// <summary>
    /// Path to the optional shared memory rules document. When relative, resolved under <see cref="BasePath"/>.
    /// Null means no memory rules document is expected.
    /// Defaults to <c>"memory-rules.md"</c>.
    /// </summary>
    public string? MemoryRulesPath { get; set; } = "memory-rules.md";
}
