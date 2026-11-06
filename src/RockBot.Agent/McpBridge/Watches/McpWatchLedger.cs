using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RockBot.Agent.McpBridge.Watches;

/// <summary>
/// The durable record of every resource watch (<c>mcp_watch_resource</c>; see
/// <c>design/mcp-bridge.md</c>, "Resource watches"). A subscription is only an open stream, so
/// nothing about it survives a restart on either side: this file is how a restarted bridge knows
/// which resources to subscribe to again, for which session, and what each looked like last.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Write-ahead.</b> An entry is written before the agent is told it watches, and its
///   ending before the session is told it ended.</item>
///   <item><b>Hashes, never content.</b> An entry holds the hash of the content last delivered,
///   not the content: a change missed while the bridge was down is found by reading the resource
///   again and comparing.</item>
///   <item><b>Settles when its ending is delivered</b> (or needs no delivery), then is purged.</item>
/// </list>
/// The file carries a top-level <c>version</c> (<see cref="CurrentVersion"/>) and follows
/// <c>design/schema-migrations.md</c>.
/// </remarks>
internal sealed class McpWatchLedger
{
    /// <summary>Version of the ledger file's shape.</summary>
    public const int CurrentVersion = 1;

    private readonly JsonLedger<McpWatchEntry> _ledger;

    public McpWatchLedger(string path, ILogger logger, TimeProvider? time = null) =>
        _ledger = new JsonLedger<McpWatchEntry>(path, "MCP watch ledger", CurrentVersion, logger, time);

    /// <summary>Where the ledger lives.</summary>
    public string Path => _ledger.Path;

    public Task<IReadOnlyList<McpWatchEntry>> ReadAllAsync(CancellationToken ct = default) => _ledger.ReadAllAsync(ct);

    public Task<McpWatchEntry?> GetAsync(string watchId, CancellationToken ct = default) => _ledger.GetAsync(watchId, ct);

    public Task AddAsync(McpWatchEntry entry, CancellationToken ct = default) => _ledger.AddAsync(entry, ct);

    public Task<bool> UpdateAsync(string watchId, Action<McpWatchEntry> update, CancellationToken ct = default) =>
        _ledger.UpdateAsync(watchId, update, ct);

    /// <summary>Records the hash of the content last delivered to the session.</summary>
    public Task<bool> SetLastHashAsync(string watchId, string hash, CancellationToken ct = default) =>
        UpdateAsync(watchId, e => e.LastHash = hash, ct);

    /// <summary>
    /// Records that the watch ended. With <paramref name="notify"/>, it stays unsettled until
    /// <see cref="MarkSettledAsync"/> (its ending must reach the session); otherwise it settles now.
    /// </summary>
    public Task<bool> EndAsync(string watchId, string reason, bool notify, CancellationToken ct = default) =>
        UpdateAsync(watchId, e =>
        {
            if (e.Status == McpWatchEntryStatus.Ended)
                return;
            var now = _ledger.Time.GetUtcNow();
            e.Status = McpWatchEntryStatus.Ended;
            e.Reason = reason;
            e.NotifySession = notify;
            e.EndedAt = now;
            if (!notify)
                e.ResolvedAt = now;
        }, ct);

    /// <summary>Records that the watch's ending reached its session; the entry is then settled.</summary>
    public Task<bool> MarkSettledAsync(string watchId, CancellationToken ct = default) =>
        UpdateAsync(watchId, e => e.ResolvedAt ??= _ledger.Time.GetUtcNow(), ct);
}

/// <summary>Status values of an <see cref="McpWatchEntry"/>.</summary>
internal static class McpWatchEntryStatus
{
    /// <summary>Watched: the bridge subscribes to it and delivers its changes.</summary>
    public const string Active = "active";

    /// <summary>No longer watched (unwatched, gone, expired, or its server removed).</summary>
    public const string Ended = "ended";
}

/// <summary>One watch: a session that wants to hear when one resource on one server changes.</summary>
internal sealed class McpWatchEntry : ILedgerEntry<McpWatchEntry>
{
    /// <summary>The bridge's id for the watch (<c>w_…</c>), the one the agent sees.</summary>
    public string WatchId { get; set; } = "";

    public string Status { get; set; } = McpWatchEntryStatus.Active;

    /// <summary>The run namespace that watches: <c>session/{id}</c> or <c>subagent/{id}</c>.</summary>
    public string SessionId { get; set; } = "";

    public string Server { get; set; } = "";

    /// <summary>The server entry's stable id, so a rename still finds it.</summary>
    public string? ServerId { get; set; }

    /// <summary>The watched resource's URI, as the agent gave it.</summary>
    public string Uri { get; set; } = "";

    /// <summary>Hash of the content last delivered to the session (or read when the watch began).</summary>
    public string? LastHash { get; set; }

    /// <summary>The watching run's consequential-action origin, if it stated one (#685).</summary>
    public string? RunOrigin { get; set; }

    /// <summary>What the originating user request asked for, if stated.</summary>
    public string? UserAskedFor { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the watch ends on its own.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Why it ended.</summary>
    public string? Reason { get; set; }

    /// <summary>Its ending must be delivered to the session (the agent didn't end it itself).</summary>
    public bool NotifySession { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Settled: ended, and its ending delivered when it needed to be.</summary>
    [JsonIgnore]
    public bool IsSettled => ResolvedAt is not null;

    [JsonIgnore]
    public bool IsActive => Status == McpWatchEntryStatus.Active;

    string ILedgerEntry<McpWatchEntry>.Key => WatchId;

    public McpWatchEntry Clone() =>
        JsonSerializer.Deserialize<McpWatchEntry>(JsonSerializer.Serialize(this))!;
}
