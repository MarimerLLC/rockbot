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
    /// e.g. <c>session/abc123</c> or <c>subagent/task1</c>) that the agent saved on purpose —
    /// everything outside <see cref="EvictableCategories"/>. Regenerable content has its own
    /// budget (<see cref="MaxRegenerableEntriesPerNamespace"/>) and never counts here, so page
    /// chunks can't crowd out the agent's notes (#803). A new key past this limit is refused,
    /// which <c>TrySetAsync</c> reports. Defaults to 50.
    /// </summary>
    public int MaxEntriesPerNamespace { get; set; } = 50;

    /// <summary>
    /// Maximum number of live regenerable entries (<see cref="EvictableCategories"/>: page
    /// chunks, chunked tool results, chunked wisp input) per namespace. Once it is reached, a
    /// new regenerable key makes room by evicting the least recently read chunk group (one page
    /// or result, every chunk and its index, at once), never the group being written; only when
    /// nothing else is left is it refused. Long pages run 17–38 chunks, so the default of 400
    /// keeps ten or more of them readable within their TTL (#803). The cache is in process and
    /// chunks expire (20 minutes for <c>web_browse</c>), which bounds the memory this holds.
    /// </summary>
    public int MaxRegenerableEntriesPerNamespace { get; set; } = 400;

    /// <summary>The evictable categories used when <see cref="EvictableCategories"/> is not set.</summary>
    public static readonly IReadOnlyList<string> DefaultEvictableCategories = ["web", "tool-result", "wisp-input"];

    /// <summary>
    /// Categories of entries that are copies of content the agent can fetch or produce again —
    /// page chunks from <c>web_browse</c>, chunked tool results, chunked wisp input. Matched
    /// case-insensitively. They count against <see cref="MaxRegenerableEntriesPerNamespace"/>
    /// rather than <see cref="MaxEntriesPerNamespace"/>, and when that budget is full they give
    /// way to new ones, least recently read first and a whole chunk group (one page or result)
    /// at a time (#801, #803). Anything else (what the agent saved on purpose) is never
    /// evicted. <c>null</c> means
    /// <see cref="DefaultEvictableCategories"/>; a configured list replaces the default. These
    /// entries are not persisted to disk. Configuration binding can't produce an empty list (an
    /// empty JSON array binds as <c>null</c>), so to turn eviction off from configuration list a
    /// category nothing uses, e.g. <c>none</c>.
    /// </summary>
    public IList<string>? EvictableCategories { get; set; }

    /// <summary>The evictable categories in effect: the configured list, or the default.</summary>
    public IReadOnlyList<string> EffectiveEvictableCategories =>
        EvictableCategories is { } configured
            ? configured.Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
            : DefaultEvictableCategories;

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
