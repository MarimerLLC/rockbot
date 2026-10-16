using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RockBot.Agent.McpBridge.Handback;

/// <summary>
/// The durable record of every question the bridge has handed back (see
/// <c>design/mcp-elicitation-handback.md</c>, "Pending ledger and restart recovery"). A parked call
/// lives only in the bridge's memory; this file is how a restarted bridge knows which calls a
/// restart interrupted, and what they were doing.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Write-ahead.</b> An entry is written before its question is handed back, so every
///   question an agent has seen has an entry.</item>
///   <item><b>Crash-safe.</b> Each write goes to a temporary file that is renamed over the old
///   one, so a crash mid-write leaves the previous ledger intact.</item>
///   <item><b>Single writer.</b> Only the bridge writes, and its writes are serialized.</item>
///   <item><b>Bounded.</b> At most <c>MaxPendingQuestions</c> entries are pending at a time, and
///   settled entries are purged <see cref="RetainSettled"/> after they settle.</item>
///   <item><b>Context, never answers.</b> Entries hold what was asked and why; answer values are
///   never written.</item>
/// </list>
/// The file carries a top-level <c>version</c> (<see cref="CurrentVersion"/>). Adding an optional
/// property is absorbed by the tolerant reader; anything else bumps the version and ships a
/// migration (<c>design/schema-migrations.md</c>).
/// </remarks>
internal sealed class PendingQuestionLedger
{
    /// <summary>Version of the ledger file's shape.</summary>
    public const int CurrentVersion = 1;

    /// <summary>How long a settled entry is kept before it is purged.</summary>
    public static readonly TimeSpan RetainSettled = JsonLedger<PendingQuestionEntry>.RetainSettled;

    private readonly JsonLedger<PendingQuestionEntry> _ledger;

    public PendingQuestionLedger(string path, ILogger logger, TimeProvider? time = null) =>
        _ledger = new JsonLedger<PendingQuestionEntry>(path, "MCP pending-question ledger", CurrentVersion, logger, time);

    /// <summary>Where the ledger lives.</summary>
    public string Path => _ledger.Path;

    /// <summary>A copy of every entry.</summary>
    public Task<IReadOnlyList<PendingQuestionEntry>> ReadAllAsync(CancellationToken ct = default) =>
        _ledger.ReadAllAsync(ct);

    /// <summary>A copy of one entry, or null.</summary>
    public Task<PendingQuestionEntry?> GetAsync(string questionId, CancellationToken ct = default) =>
        _ledger.GetAsync(questionId, ct);

    /// <summary>Adds an entry and writes the ledger before returning.</summary>
    public Task AddAsync(PendingQuestionEntry entry, CancellationToken ct = default) =>
        _ledger.AddAsync(entry, ct);

    /// <summary>
    /// Applies <paramref name="update"/> to the entry for <paramref name="questionId"/>, if there is
    /// one, and writes the ledger. Returns whether the entry existed.
    /// </summary>
    public Task<bool> UpdateAsync(string questionId, Action<PendingQuestionEntry> update, CancellationToken ct = default) =>
        _ledger.UpdateAsync(questionId, update, ct);

    /// <summary>Settles a pending entry with <paramref name="status"/>, unless it has already settled.</summary>
    public Task SettleAsync(string questionId, string status, string? reason = null, CancellationToken ct = default) =>
        UpdateAsync(questionId, entry =>
        {
            if (entry.Status != PendingQuestionStatus.Pending)
                return;
            entry.Status = status;
            entry.Reason = reason;
            entry.ResolvedAt = _ledger.Time.GetUtcNow();
        }, ct);

    /// <summary>
    /// Applies <paramref name="update"/> to every entry in one write (startup reconciliation).
    /// </summary>
    public Task UpdateAllAsync(Action<PendingQuestionEntry> update, CancellationToken ct = default) =>
        _ledger.UpdateAllAsync(update, ct);
}

/// <summary>Status values of a <see cref="PendingQuestionEntry"/>.</summary>
internal static class PendingQuestionStatus
{
    /// <summary>Handed back; waiting for <c>mcp_answer</c>.</summary>
    public const string Pending = "pending";

    /// <summary>The agent answered and the answer went to the server.</summary>
    public const string Answered = "answered";

    /// <summary>The agent declined the question.</summary>
    public const string Declined = "declined";

    /// <summary>The question was open longer than its TTL, so the call was cancelled.</summary>
    public const string Expired = "expired";

    /// <summary>The call was cancelled for another reason (its server was removed); see <c>reason</c>.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A restart interrupted the call; its session hasn't been told yet.</summary>
    public const string Interrupted = "interrupted";

    /// <summary>A restart interrupted the call and its session has been told.</summary>
    public const string Notified = "notified";
}

/// <summary>One handed-back question, with enough about its call for a restarted agent to act on.</summary>
internal sealed class PendingQuestionEntry : ILedgerEntry<PendingQuestionEntry>
{
    public string QuestionId { get; set; } = "";

    /// <summary>
    /// Set when the question came from a background task (its bridge task id). A restart doesn't
    /// end such a call: the task lives on the server, which asks again on the next poll, so the
    /// question is closed quietly rather than announced as an interrupted call.
    /// </summary>
    public string? TaskId { get; set; }
    public string Status { get; set; } = PendingQuestionStatus.Pending;
    public string? SessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public PendingCallContext Call { get; set; } = new();
    public PendingTrigger? TriggeredBy { get; set; }
    public PendingQuestionText Question { get; set; } = new();

    /// <summary>Why the entry settled the way it did, when that isn't obvious from the status.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset? NotifiedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>Settled: nothing more will happen to it but the purge.</summary>
    [JsonIgnore]
    public bool IsSettled => Status is not (PendingQuestionStatus.Pending or PendingQuestionStatus.Interrupted);

    string ILedgerEntry<PendingQuestionEntry>.Key => QuestionId;

    public PendingQuestionEntry Clone() =>
        JsonSerializer.Deserialize<PendingQuestionEntry>(JsonSerializer.Serialize(this))!;
}

/// <summary>The call that asked: what was being done, and how to redo it.</summary>
internal sealed class PendingCallContext
{
    public string Server { get; set; } = "";
    public string Tool { get; set; } = "";

    /// <summary>The tool's own description, truncated, because a restarted agent may not have its schema.</summary>
    public string? ToolDescription { get; set; }

    /// <summary>The arguments the agent sent, credential-named keys redacted and secrets scrubbed.</summary>
    public string? Arguments { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public int Round { get; set; } = 1;

    /// <summary>Earlier questions in the same call: what was asked and the names of the fields answered.</summary>
    public List<PendingEarlierRound> EarlierRounds { get; set; } = [];
}

/// <summary>An earlier question in the same call. Field names only, never values.</summary>
internal sealed class PendingEarlierRound
{
    public string Message { get; set; } = "";
    public string Action { get; set; } = "";
    public List<string> AnsweredFields { get; set; } = [];
}

/// <summary>The user's request the call was serving.</summary>
internal sealed class PendingTrigger
{
    public DateTimeOffset At { get; set; }

    /// <summary>The last user message before the call, scrubbed and capped.</summary>
    public string UserExcerpt { get; set; } = "";
}

/// <summary>The server's question, flattened.</summary>
internal sealed class PendingQuestionText
{
    public string Message { get; set; } = "";
    public List<PendingQuestionField> Fields { get; set; } = [];
}

internal sealed class PendingQuestionField
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Required { get; set; }
}
