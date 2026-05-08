namespace RockBot.Observation;

/// <summary>
/// Top-level persistent state for one observation target. Serialised to the
/// target's configured JSON state file. The file is the source of truth;
/// the regenerated markdown is a derived artifact that can be rebuilt
/// deterministically from this state.
/// </summary>
public sealed class ObservationState
{
    /// <summary>
    /// Current schema version. Used to route through future migrations when
    /// the shape of this record changes incompatibly.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Schema version this state file was written under. Always set to
    /// <see cref="CurrentSchemaVersion"/> by code; older values are recognized
    /// for migration.
    /// </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Timestamp of the most recent dream cycle that completed phase 1 for
    /// this target. Used by the conversation-window selection in the next
    /// cycle to avoid re-extracting the same conversations.
    /// </summary>
    public DateTimeOffset? LastDreamAt { get; set; }

    /// <summary>
    /// Candidate observations: extracted but not yet reinforced enough to
    /// promote. Counted for promotion-threshold checks; aged out per the
    /// candidate-aging window.
    /// </summary>
    public List<Candidate> Candidates { get; init; } = [];

    /// <summary>
    /// Promoted theories: candidates that crossed the promotion threshold
    /// and were validated by the evaluation pass. These are what populate
    /// the "Theories" section of the regenerated markdown.
    /// </summary>
    public List<Theory> Theories { get; init; } = [];

    /// <summary>
    /// Historical snapshots of the regenerated markdown body, oldest first.
    /// Length is bounded by the target's <c>SnapshotRetentionCount</c>.
    /// </summary>
    public List<Snapshot> Snapshots { get; init; } = [];
}
