namespace RockBot.Observation;

/// <summary>
/// Historical snapshot of the regenerated markdown output. The framework
/// retains the last N snapshots in <see cref="ObservationState.Snapshots"/>
/// so evolution over time is observable without external tooling. Snapshots
/// are appended at the end of phase 2 (after a fresh markdown render);
/// oldest entries are evicted to maintain the configured cap.
/// </summary>
/// <param name="TakenAt">When the snapshot was captured.</param>
/// <param name="Markdown">Full text of the regenerated markdown body at that time.</param>
public sealed record Snapshot(
    DateTimeOffset TakenAt,
    string Markdown);
