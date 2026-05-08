namespace RockBot.Host;

/// <summary>
/// Aggregate state for a stream of post-recovery tool failures sharing the same
/// <see cref="ClusterKey"/>. Tracked in-process by <see cref="IFailureClusterStore"/>
/// and persisted to the PVC so cluster history survives agent restarts.
/// See <c>design/self-repair.md</c> Phase 5.
/// </summary>
/// <param name="Key">Cluster identity (server, tool, error class).</param>
/// <param name="Count">Total number of failures recorded for this cluster.</param>
/// <param name="SessionIds">Distinct session ids that contributed at least one failure. Bounded by <see cref="FailureClusterOptions.MaxSessionIdsPerCluster"/>.</param>
/// <param name="FirstSeen">UTC timestamp of the first recorded failure.</param>
/// <param name="LastSeen">UTC timestamp of the most recent recorded failure.</param>
/// <param name="SampleErrorMessages">Most recent distinct error messages, oldest-first. Bounded by <see cref="FailureClusterOptions.MaxSampleMessages"/> with each entry truncated.</param>
public sealed record FailureCluster(
    ClusterKey Key,
    int Count,
    IReadOnlySet<string> SessionIds,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<string> SampleErrorMessages);
