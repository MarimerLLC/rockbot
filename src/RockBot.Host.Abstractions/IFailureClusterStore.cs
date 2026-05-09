namespace RockBot.Host;

/// <summary>
/// Tracks tool failure clusters in-process for hot reads/writes, with PVC-backed
/// persistence for crash recovery. The MCP gateway records every post-recovery
/// failure here; the dream service reads clusters to drive repair tickets.
/// Auto-recovered calls are NOT recorded — they live in the recovery telemetry
/// metrics counter only. See <c>design/self-repair.md</c> Phase 5.
/// </summary>
public interface IFailureClusterStore
{
    /// <summary>
    /// Increments or creates the cluster identified by <paramref name="key"/>,
    /// adding <paramref name="sessionId"/> (when non-null) to the set of sessions
    /// that have produced this failure and appending <paramref name="errorMessage"/>
    /// to the bounded sample buffer.
    /// </summary>
    /// <param name="key">Cluster identity.</param>
    /// <param name="sessionId">Originating session, or null when the call was outside a session context.</param>
    /// <param name="errorMessage">Raw error text (truncated by the store).</param>
    /// <param name="at">Timestamp of the failure (typically <see cref="DateTimeOffset.UtcNow"/>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordAsync(
        ClusterKey key,
        string? sessionId,
        string errorMessage,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a snapshot of every cluster currently tracked, ordered by
    /// <see cref="FailureCluster.LastSeen"/> descending.
    /// </summary>
    Task<IReadOnlyList<FailureCluster>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of clusters that meet the escalation thresholds
    /// configured in <see cref="FailureClusterOptions"/> — by default
    /// <c>Count >= 3 &amp;&amp; SessionIds.Count >= 2 &amp;&amp; (now - LastSeen) &lt; 24h</c>.
    /// </summary>
    /// <param name="now">The reference time used to evaluate the recency window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<FailureCluster>> GetEscalatableAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
