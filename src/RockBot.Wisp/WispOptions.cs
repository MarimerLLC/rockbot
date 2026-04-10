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

    /// <summary>
    /// Maximum number of wisps to execute concurrently within a single batch.
    /// The caller can submit any number of definitions; the system gates execution
    /// to this limit using a semaphore.
    /// </summary>
    public int MaxConcurrentWisps { get; set; } = 10;
}
