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

    /// <summary>
    /// When true, wisp runs originating from a scheduled-task session
    /// (sessionId prefixed with <c>patrol/</c>) eagerly attach their body as a
    /// provisional resource on the originating skill once the shape has succeeded
    /// at least <see cref="EagerScheduledTaskPromotionThreshold"/> times. Bypasses
    /// the slower dream-pass promotion path so recurring scheduled work captures
    /// reusable assets after the first couple of fires instead of waiting for a
    /// nightly dream cycle.
    /// </summary>
    public bool EagerScheduledTaskPromotionEnabled { get; set; } = true;

    /// <summary>
    /// Minimum same-shape successful runs required before a scheduled-task wisp is
    /// eagerly attached as a provisional skill resource. Defaults to 2 (record on
    /// the first success, promote on the second).
    /// </summary>
    public int EagerScheduledTaskPromotionThreshold { get; set; } = 2;

    /// <summary>
    /// Session-id prefix that marks a scheduled-task execution. The
    /// <see cref="RockBot.Agent.ScheduledTaskHandler"/> uses
    /// <c>patrol/{taskName}</c>; if that convention changes, update this default.
    /// </summary>
    public string ScheduledTaskSessionPrefix { get; set; } = "patrol/";
}
