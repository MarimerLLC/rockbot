namespace RockBot.Host;

/// <summary>
/// Configuration for global working memory.
/// </summary>
public sealed class WorkingMemoryOptions
{
    /// <summary>Default TTL when callers do not specify one. Defaults to 5 minutes.</summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum TTL for entries written through <c>save_to_working_memory</c> by background
    /// subagents and workers. Their findings are read back by the parent only after every
    /// sibling in the batch has finished and the consolidated synthesis runs, so a TTL sized
    /// to the subagent's own step (or the 5-minute default) can lapse before the parent gets
    /// to it. Shorter requested TTLs are raised to this value. Defaults to 4 hours.
    /// </summary>
    public TimeSpan BackgroundTaskMinimumTtl { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Maximum number of live entries per namespace (first two key path segments,
    /// e.g. <c>session/abc123</c> or <c>subagent/task1</c>). New entries are rejected
    /// (with a warning) once this limit is reached. Defaults to 50.
    /// </summary>
    public int MaxEntriesPerNamespace { get; set; } = 50;

    /// <summary>
    /// Base directory for persisting working memory to disk.
    /// Defaults to <c>"working-memory"</c>, resolved under <see cref="AgentProfileOptions.BasePath"/>.
    /// Set to an absolute path to override. Entries are grouped by top-level key segment
    /// (e.g. <c>session.json</c>, <c>patrol.json</c>, <c>subagent.json</c>).
    /// </summary>
    public string BasePath { get; set; } = "working-memory";

    /// <summary>The snapshot prefixes used when <see cref="SnapshotKeyPrefixes"/> is not set.</summary>
    public static readonly IReadOnlyList<string> DefaultSnapshotKeyPrefixes = ["shared/patrol/"];

    /// <summary>
    /// Key prefixes whose entries are point-in-time snapshots of state that lives somewhere
    /// else (todos, calendar, mail) — e.g. what the heartbeat patrol writes under
    /// <c>shared/patrol/</c>. <c>get_from_working_memory</c> wraps these in a banner naming
    /// the writer and the snapshot's age, so a cached copy cannot pass for live state
    /// (issue #668). <c>null</c> (the default) means <see cref="DefaultSnapshotKeyPrefixes"/>;
    /// a configured list replaces the default rather than adding to it.
    /// </summary>
    public IList<string>? SnapshotKeyPrefixes { get; set; }

    /// <summary>
    /// Age past which a snapshot entry (see <see cref="SnapshotKeyPrefixes"/>) is flagged
    /// STALE when read. The data is still returned; only the banner changes. Defaults to
    /// 6 hours — a little over one heartbeat-patrol cycle.
    /// </summary>
    public TimeSpan SnapshotStaleAfter { get; set; } = TimeSpan.FromHours(6);

    /// <summary>The snapshot prefixes in effect: the configured list, or the default.</summary>
    public IReadOnlyList<string> EffectiveSnapshotKeyPrefixes =>
        SnapshotKeyPrefixes is { Count: > 0 } configured
            ? configured.Where(p => !string.IsNullOrWhiteSpace(p)).ToList()
            : DefaultSnapshotKeyPrefixes;
}
