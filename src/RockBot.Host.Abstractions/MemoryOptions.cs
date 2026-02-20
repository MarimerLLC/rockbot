namespace RockBot.Host;

/// <summary>
/// Options for long-term memory storage.
/// When <see cref="BasePath"/> is relative, it is resolved under <see cref="AgentProfileOptions.BasePath"/>.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>
    /// Base directory for memory files. Defaults to <c>"memory"</c>.
    /// When relative, resolved under the agent profile base path.
    /// When absolute, used directly.
    /// </summary>
    public string BasePath { get; set; } = "memory";
}
