namespace RockBot.Wisp;

/// <summary>
/// Configuration options for the wisp executor subsystem.
/// </summary>
public sealed class WispOptions
{
    /// <summary>
    /// Root directory for shared volume file I/O. Wisp steps with output_to/input_from
    /// resolve paths relative to this base. When null, file I/O is skipped and data
    /// passes only through working memory.
    /// </summary>
    public string? SharedVolumePath { get; set; }
}
