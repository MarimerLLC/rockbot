namespace RockBot.Host;

/// <summary>
/// Options for the failure cluster store. When <see cref="BasePath"/> is relative
/// it is resolved under <see cref="AgentProfileOptions.BasePath"/>, mirroring
/// <see cref="MemoryOptions"/>.
/// </summary>
public sealed class FailureClusterOptions
{
    /// <summary>
    /// Base directory for cluster state files. Defaults to <c>"telemetry"</c>.
    /// When relative, resolved under the agent profile base path
    /// (<c>/data/agent/telemetry</c> in K8s).
    /// </summary>
    public string BasePath { get; set; } = "telemetry";

    /// <summary>
    /// How often the in-memory cluster state is flushed to a snapshot file and
    /// the JSONL log truncated. Default 30 seconds.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum number of sample error messages retained per cluster. Default 5.
    /// Most recent messages are kept; older messages are dropped on overflow.
    /// </summary>
    public int MaxSampleMessages { get; set; } = 5;

    /// <summary>
    /// Maximum length per sample error message (characters). Longer messages are
    /// truncated with an ellipsis. Default 512.
    /// </summary>
    public int MaxSampleMessageLength { get; set; } = 512;

    /// <summary>
    /// Maximum number of distinct session ids retained per cluster. Once this
    /// cap is reached, additional sessions still increment <see cref="FailureCluster.Count"/>
    /// but do not grow the set. Default 64.
    /// </summary>
    public int MaxSessionIdsPerCluster { get; set; } = 64;

    /// <summary>
    /// Minimum failure count for a cluster to be reported as escalatable.
    /// Default 3 (matches the Phase 5 acceptance criterion).
    /// </summary>
    public int EscalationCountThreshold { get; set; } = 3;

    /// <summary>
    /// Minimum number of distinct sessions for a cluster to be reported as
    /// escalatable. Default 2.
    /// </summary>
    public int EscalationSessionThreshold { get; set; } = 2;

    /// <summary>
    /// Maximum age of <see cref="FailureCluster.LastSeen"/> for a cluster to be
    /// reported as escalatable. Default 24 hours.
    /// </summary>
    public TimeSpan EscalationWindow { get; set; } = TimeSpan.FromHours(24);
}
