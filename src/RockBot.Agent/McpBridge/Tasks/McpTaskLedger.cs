using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RockBot.Agent.McpBridge.Handback;

namespace RockBot.Agent.McpBridge.Tasks;

/// <summary>
/// The durable record of every background task the bridge started (see <c>design/mcp-bridge.md</c>,
/// "Long-running tools"). A task lives on its server, not in the bridge, so unlike a parked call it
/// survives a restart: this file is how a restarted bridge knows which tasks to poll again, and
/// which session each belongs to.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Write-ahead.</b> An entry is written before the "started" result reaches the agent,
///   so every task an agent has been told about has an entry.</item>
///   <item><b>Settles on delivery.</b> An entry stays open until its ending has been delivered to
///   its session; a restart in between delivers it again (at least once).</item>
///   <item><b>Context, never results.</b> Entries hold what was called and why, with arguments
///   redacted; the task's result and any answers given to it are never written.</item>
/// </list>
/// The file carries a top-level <c>version</c> (<see cref="CurrentVersion"/>) and follows
/// <c>design/schema-migrations.md</c>.
/// </remarks>
internal sealed class McpTaskLedger
{
    /// <summary>Version of the ledger file's shape.</summary>
    public const int CurrentVersion = 1;

    private readonly JsonLedger<McpTaskEntry> _ledger;

    public McpTaskLedger(string path, ILogger logger, TimeProvider? time = null) =>
        _ledger = new JsonLedger<McpTaskEntry>(path, "MCP task ledger", CurrentVersion, logger, time);

    /// <summary>Where the ledger lives.</summary>
    public string Path => _ledger.Path;

    public Task<IReadOnlyList<McpTaskEntry>> ReadAllAsync(CancellationToken ct = default) => _ledger.ReadAllAsync(ct);

    public Task<McpTaskEntry?> GetAsync(string taskId, CancellationToken ct = default) => _ledger.GetAsync(taskId, ct);

    public Task AddAsync(McpTaskEntry entry, CancellationToken ct = default) => _ledger.AddAsync(entry, ct);

    public Task<bool> UpdateAsync(string taskId, Action<McpTaskEntry> update, CancellationToken ct = default) =>
        _ledger.UpdateAsync(taskId, update, ct);

    /// <summary>Records how the task ended, before its ending is delivered.</summary>
    public Task EndAsync(string taskId, string status, string? reason, CancellationToken ct = default) =>
        UpdateAsync(taskId, e =>
        {
            if (e.DeliveredAt is not null)
                return;
            e.Status = status;
            e.Reason = reason;
            e.EndedAt ??= _ledger.Time.GetUtcNow();
        }, ct);

    /// <summary>Records that the task's ending reached its session; the entry is then settled.</summary>
    public Task MarkDeliveredAsync(string taskId, CancellationToken ct = default) =>
        UpdateAsync(taskId, e =>
        {
            var now = _ledger.Time.GetUtcNow();
            e.DeliveredAt ??= now;
            e.ResolvedAt ??= now;
        }, ct);
}

/// <summary>Status values of an <see cref="McpTaskEntry"/>.</summary>
internal static class McpTaskEntryStatus
{
    /// <summary>Started on the server; the bridge polls it.</summary>
    public const string Running = "running";

    /// <summary>The server finished it with a result (which may be an error result).</summary>
    public const string Completed = "completed";

    /// <summary>The server reported that it failed.</summary>
    public const string Failed = "failed";

    /// <summary>Cancelled: by the agent, because its server was removed, or by the server.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The server no longer has it, or can't be reached for it any more.</summary>
    public const string Lost = "lost";

    /// <summary>It ran longer than its server's <c>taskTimeoutMinutes</c>, so the bridge cancelled it.</summary>
    public const string TimedOut = "timed-out";

    /// <summary>Endings the server reports, which a restarted bridge can ask it for again.</summary>
    public static bool IsServerReported(string status) => status is Completed or Failed;
}

/// <summary>One background task, with enough about its call for a restarted bridge to carry on.</summary>
internal sealed class McpTaskEntry : ILedgerEntry<McpTaskEntry>
{
    /// <summary>The bridge's own id (<c>t_…</c>), the one the agent sees.</summary>
    public string TaskId { get; set; } = "";

    /// <summary>The server's id for the task, used for <c>tasks/get</c>, <c>tasks/update</c> and <c>tasks/cancel</c>.</summary>
    public string ServerTaskId { get; set; } = "";

    public string Status { get; set; } = McpTaskEntryStatus.Running;

    /// <summary>The run namespace that started it: <c>session/{id}</c> or <c>subagent/{id}</c>.</summary>
    public string SessionId { get; set; } = "";

    public string Server { get; set; } = "";

    /// <summary>The server entry's stable id, so a rename still finds it.</summary>
    public string? ServerId { get; set; }

    public string Tool { get; set; } = "";

    /// <summary>The tool's own description, truncated.</summary>
    public string? ToolDescription { get; set; }

    /// <summary>The arguments the agent sent, credential-named keys redacted and secrets scrubbed.</summary>
    public string? Arguments { get; set; }

    /// <summary>The last thing the user said before the call, for the record.</summary>
    public PendingTrigger? TriggeredBy { get; set; }

    /// <summary>The caller could answer a handed-back question (<c>rb-mcp-handback</c>).</summary>
    public bool HandbackCapable { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the bridge gives up on it and cancels it.</summary>
    public DateTimeOffset Deadline { get; set; }

    /// <summary>The server's suggested poll interval, if it gave one.</summary>
    public long? PollIntervalMs { get; set; }

    /// <summary>Input request keys already answered, so a later poll doesn't ask them again.</summary>
    public List<string> ResolvedInputKeys { get; set; } = [];

    /// <summary>Why it ended the way it did, when that isn't obvious from the status.</summary>
    public string? Reason { get; set; }

    /// <summary>The ending was the agent's own request (<c>mcp_cancel_task</c>).</summary>
    public bool CancelledByAgent { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Settled: its ending reached the session, and nothing remains but the purge.</summary>
    [JsonIgnore]
    public bool IsSettled => DeliveredAt is not null;

    string ILedgerEntry<McpTaskEntry>.Key => TaskId;

    public McpTaskEntry Clone() =>
        JsonSerializer.Deserialize<McpTaskEntry>(JsonSerializer.Serialize(this))!;
}
