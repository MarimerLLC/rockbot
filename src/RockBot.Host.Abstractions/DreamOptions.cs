namespace RockBot.Host;

/// <summary>
/// Options for the periodic memory consolidation service (dreaming).
/// </summary>
public sealed class DreamOptions
{
    /// <summary>Whether dreaming is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to wait after startup before the first dream cycle.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often dream cycles run.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Path to the dream directive file, relative to <see cref="AgentProfileOptions.BasePath"/>.
    /// </summary>
    public string DirectivePath { get; set; } = "dream.md";
}
