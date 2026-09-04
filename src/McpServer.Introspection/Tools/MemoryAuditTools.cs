using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RockBot.Host;

namespace McpServer.Introspection.Tools;

/// <summary>
/// MCP-facing view of the memory audit's files on the agent profile volume.
/// <para>
/// The audit writes; this reads. Keeping the reader in the sidecar means the agent can answer
/// "are you losing memories?" with a measurement instead of a recall query — and answer it even
/// while its own memory is the thing under suspicion.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class MemoryAuditTools(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private string AuditPath => configuration["MemoryAudit:Path"] ?? "/data/agent/memory-audit";

    [McpServerTool(Name = "get_memory_audit")]
    [Description(
        "Returns the most recent memory-health audit: a plain-language report plus the raw " +
        "snapshot. Covers live and archived entry counts, what was created/archived/deleted " +
        "since the previous run, hard deletes the retention purge cannot account for, merge " +
        "chain depth, near-duplicate counts, the purge outlook, and any invariants that failed. " +
        "Each finding comes with a plain-language explanation and what to do about it, so you can " +
        "answer a non-technical question about any warning without looking anything else up. " +
        "Use this for questions about memory health, memory loss, or consolidation behaviour — " +
        "NOT the recall tools, which search memory contents rather than measuring the store.")]
    public async Task<string> GetMemoryAuditAsync()
    {
        var reportPath = Path.Combine(AuditPath, MemoryAuditFiles.LatestReport);
        var report = File.Exists(reportPath) ? await ReadTextAsync(reportPath) : null;
        var snapshots = await ReadSnapshotsAsync();
        var latest = snapshots.Count > 0 ? snapshots[^1] : null;

        if (report is null && latest is null)
            return JsonSerializer.Serialize(new
            {
                message = "No memory audit has run yet. The first audit runs on its cron schedule " +
                          "(default 04:00 daily) or shortly after the agent starts.",
                auditPath = AuditPath
            }, WriteOptions);

        return JsonSerializer.Serialize(new
        {
            report,
            snapshot = latest,
            // Every finding carries its own plain-language explanation. The invariant names are
            // jargon by design — stable identifiers to key on — and an agent asked "what does
            // chain-depth-threshold mean?" must not have to already know, or have to go and
            // fetch a second document it might not think to fetch.
            findings = Explain(latest),
            paused = await ReadPauseMarkerAsync(),
            totalSnapshots = snapshots.Count
        }, WriteOptions);
    }

    /// <summary>
    /// Pairs each violation on <paramref name="snapshot"/> with its glossary entry. Returns null
    /// when the run was clean, so a healthy report does not carry an empty list the agent might
    /// read as a finding.
    /// </summary>
    private static object? Explain(MemoryAuditSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.Invariants.Count == 0) return null;

        return snapshot.Invariants.Select(v =>
        {
            var definition = MemoryAuditGlossary.Describe(v.Name);
            return new
            {
                name = v.Name,
                title = definition?.Title,
                inPlainLanguage = definition?.WhatItMeans,
                whatToDo = definition?.WhatToDo,
                severity = definition?.Severity,
                technicalDetail = v.Message,
                affectedIds = v.Ids
            };
        }).ToList();
    }

    [McpServerTool(Name = "get_memory_audit_trend")]
    [Description(
        "Returns the memory audit's snapshot rows for the last N days, oldest first — one row " +
        "per audit run. Use this to answer whether the corpus is growing, shrinking, or " +
        "converging over time, and to see whether a loss was a one-off or a slope. days is " +
        "clamped to 1..400.")]
    public async Task<string> GetMemoryAuditTrendAsync(
        [Description("Window in days (1..400, default 30).")] int days = 30)
    {
        days = Math.Clamp(days, 1, 400);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        var snapshots = await ReadSnapshotsAsync();
        var windowed = snapshots.Where(s => s.TakenAt >= cutoff).ToList();

        return JsonSerializer.Serialize(new
        {
            windowDays = days,
            returned = windowed.Count,
            totalSnapshots = snapshots.Count,
            snapshots = windowed
        }, WriteOptions);
    }

    [McpServerTool(Name = "get_memory_audit_eval")]
    [Description(
        "Returns the most recent LLM-judged sample eval of memory-management decisions: per-sample " +
        "verdicts on recent merges, near-duplicates left in place, heavily reinforced entries, and " +
        "facts dropped as ephemeral, plus the overall soundness rates. Answers whether memory " +
        "management is making GOOD decisions, where get_memory_audit answers what it DID.")]
    public async Task<string> GetMemoryAuditEvalAsync()
    {
        var path = Path.Combine(AuditPath, MemoryAuditFiles.EvalLatest);
        if (!File.Exists(path))
            return JsonSerializer.Serialize(new
            {
                message = "No sample eval has run yet. It runs weekly and is skipped entirely on " +
                          "weeks where the memory corpus has not changed.",
                auditPath = AuditPath
            }, WriteOptions);

        try
        {
            var json = await ReadTextAsync(path);
            var result = JsonSerializer.Deserialize<MemoryAuditEvalResult>(json, ReadOptions);
            return JsonSerializer.Serialize(result, WriteOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new { error = $"Could not read the eval file: {ex.Message}", path }, WriteOptions);
        }
    }

    [McpServerTool(Name = "resume_memory_consolidation")]
    [Description(
        "Deletes the marker file that the memory audit wrote to pause dream memory consolidation " +
        "after finding catastrophic loss, letting consolidation run again on the next dream cycle. " +
        "Only call this when the user explicitly asks to resume — the pause exists so a human " +
        "looks at what happened before the same pass runs again. Reports what the marker said.")]
    public async Task<string> ResumeMemoryConsolidationAsync()
    {
        var path = Path.Combine(AuditPath, MemoryAuditFiles.ConsolidationPausedFile);

        if (!File.Exists(path))
            return JsonSerializer.Serialize(new
            {
                resumed = false,
                message = "Memory consolidation is not paused — no marker file exists.",
                path
            }, WriteOptions);

        var removed = await ReadPauseMarkerAsync();

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                resumed = false,
                error = $"Could not delete the marker: {ex.Message}",
                path
            }, WriteOptions);
        }

        return JsonSerializer.Serialize(new
        {
            resumed = true,
            message = "Memory consolidation will run again on the next dream cycle.",
            removed
        }, WriteOptions);
    }

    // ── File reading ─────────────────────────────────────────────────────────

    private async Task<List<MemoryAuditSnapshot>> ReadSnapshotsAsync()
    {
        var path = Path.Combine(AuditPath, MemoryAuditFiles.SnapshotsFile);
        if (!File.Exists(path)) return [];

        var snapshots = new List<MemoryAuditSnapshot>();

        try
        {
            foreach (var line in await File.ReadAllLinesAsync(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var snapshot = JsonSerializer.Deserialize<MemoryAuditSnapshot>(line, ReadOptions);
                    if (snapshot is not null) snapshots.Add(snapshot);
                }
                catch (JsonException) { /* skip malformed lines */ }
            }
        }
        catch (IOException)
        {
            return snapshots;
        }

        return snapshots;
    }

    private async Task<JsonElement?> ReadPauseMarkerAsync()
    {
        var path = Path.Combine(AuditPath, MemoryAuditFiles.ConsolidationPausedFile);
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(await ReadTextAsync(path));
            return document.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads with sharing, because the agent process appends to and rewrites these files on its
    /// own schedule and the sidecar must never take a lock that could fail an audit run.
    /// </summary>
    private static async Task<string> ReadTextAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
