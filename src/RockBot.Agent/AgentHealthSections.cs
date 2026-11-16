using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Agent.McpBridge;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Builds the agent-health introspection sections (#834, stage 4): MCP servers, skills, memory
/// health and tier routing. Each reads state the agent already keeps, in memory or in its own
/// files; none calls the LLM, a remote server, or starts work (an audit, a reconnect).
/// </summary>
internal static class AgentHealthSections
{
    /// <summary>The routing window: a week of routed requests.</summary>
    internal const int RoutingWindowDays = 7;

    /// <summary>The routing log holds at most this many entries anyway (TierRoutingLogMaxEntries).</summary>
    private const int RoutingMaxEntries = 5000;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static McpIntrospection Mcp(McpBridgeService bridge) => new()
    {
        Servers = [.. bridge.GetServerStatuses().Select(s => new McpServerIntrospection
        {
            Name = s.Name,
            DisplayName = s.DisplayName,
            Transport = s.Transport,
            Host = s.Host,
            State = s.State,
            UsesOAuth = s.UsesOAuth,
            ToolCount = s.ToolCount,
            PromptCount = s.PromptCount,
            ResourceCount = s.ResourceCount,
            Version = s.Version,
            LastError = s.LastError,
            LastErrorAt = s.LastErrorAt,
        })],
    };

    public static async Task<SkillsIntrospection> SkillsAsync(ISkillStore store)
    {
        var skills = await store.ListAsync();
        var items = skills.Select(s => new SkillIntrospection
        {
            Name = s.Name,
            Summary = s.Summary,
            Source = SourceOf(s),
            CreatedAt = s.CreatedAt,
            UpdatedAt = s.UpdatedAt,
            LastUsedAt = s.LastUsedAt,
        }).ToList();

        return new SkillsIntrospection
        {
            Count = items.Count,
            Starter = items.Count(i => i.Source == "starter"),
            Learned = items.Count(i => i.Source == "learned"),
            FromMcp = items.Count(i => i.Source == "mcp"),
            Skills = items,
        };
    }

    /// <summary><c>starter</c> (ships with the agent), <c>mcp</c> (generated for a server) or <c>learned</c>.</summary>
    internal static string SourceOf(Skill skill) =>
        skill.CodeOwned ? "starter"
        : skill.Name.StartsWith("mcp/", StringComparison.OrdinalIgnoreCase) ? "mcp"
        : "learned";

    /// <summary>
    /// The latest audit snapshot (the last line of <c>snapshots.jsonl</c>) and the pause marker,
    /// read with sharing so a running audit is never blocked.
    /// </summary>
    public static async Task<MemoryIntrospection> MemoryAsync(string auditRoot, ILogger logger)
    {
        MemoryAuditSnapshot? snapshot = null;
        var snapshotsPath = Path.Combine(auditRoot, MemoryAuditFiles.SnapshotsFile);
        if (File.Exists(snapshotsPath))
        {
            try
            {
                var lastLine = (await ReadSharedAsync(snapshotsPath))
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault();
                if (lastLine is not null)
                    snapshot = JsonSerializer.Deserialize<MemoryAuditSnapshot>(lastLine, ReadOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not read the memory audit snapshots at {Path}", snapshotsPath);
            }
        }

        string? pausedReason = null;
        DateTimeOffset? pausedAt = null;
        var pausedPath = Path.Combine(auditRoot, MemoryAuditFiles.ConsolidationPausedFile);
        var paused = File.Exists(pausedPath);
        if (paused)
        {
            try
            {
                using var marker = JsonDocument.Parse(await ReadSharedAsync(pausedPath));
                if (marker.RootElement.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    pausedReason = reason.GetString();
                if (marker.RootElement.TryGetProperty("pausedAt", out var at) && at.TryGetDateTimeOffset(out var when))
                    pausedAt = when;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not read the consolidation pause marker at {Path}", pausedPath);
            }
        }

        return new MemoryIntrospection
        {
            AuditedAt = snapshot?.TakenAt,
            Status = snapshot?.Status,
            Live = snapshot?.Live ?? 0,
            Archived = snapshot?.Archived ?? 0,
            CreatedSinceLast = snapshot?.CreatedSinceLast ?? 0,
            ArchivedSinceLast = snapshot?.ArchivedSinceLast ?? 0,
            HardDeletedSinceLast = snapshot?.HardDeletedSinceLast ?? 0,
            NetGrowthPerDay = snapshot?.NetGrowthPerDay,
            NearDuplicatePairs = snapshot?.NearDupPairs ?? 0,
            PurgeDueCount = snapshot?.Purge.Count ?? 0,
            PurgeDueWithinDays = snapshot?.Purge.DueWithinDays ?? 0,
            // The finding's entry ids stay out: they point into the user's memories.
            Findings = [.. (snapshot?.Invariants ?? []).Select(i => new MemoryFindingIntrospection { Name = i.Name, Message = i.Message })],
            EvalSoundRate = snapshot?.Eval?.SoundRate,
            EvaluatedAt = snapshot?.Eval?.EvaluatedAt,
            ConsolidationLastRunAt = snapshot?.ConsolidationLastRunAt,
            ConsolidationPaused = paused,
            ConsolidationPausedReason = pausedReason,
            ConsolidationPausedAt = pausedAt,
        };
    }

    /// <summary>
    /// The last week of the routing log through <see cref="TierRoutingAnalyzer"/>, global figures
    /// only: the analysis's clusters carry sample prompts, which are the user's words.
    /// </summary>
    public static async Task<RoutingIntrospection> RoutingAsync(TierRoutingLogger log, DateTimeOffset now)
    {
        var entries = await log.ReadRecentAsync(RoutingMaxEntries, now.AddDays(-RoutingWindowDays));
        var analysis = TierRoutingAnalyzer.Analyze(entries);
        var stats = analysis.GlobalStats;

        return new RoutingIntrospection
        {
            WindowDays = RoutingWindowDays,
            From = analysis.WindowStart,
            Through = analysis.WindowEnd,
            Total = analysis.TotalEntries,
            UserMessages = stats.UserMessageCount,
            Subagents = stats.SubagentCount,
            Fallbacks = stats.FallbackCount,
            FallbackPct = stats.FallbackPct,
            Tiers = [.. stats.ByTier.Select(t => new TierRoutingIntrospection
            {
                Tier = t.Tier.ToString(),
                Count = t.Count,
                Pct = t.Pct,
                AvgLatencyMs = t.AvgLatencyMs,
                AvgInputTokens = t.AvgInputTokens,
                AvgOutputTokens = t.AvgOutputTokens,
                AvgToolCalls = t.AvgToolCalls,
            })],
        };
    }

    /// <summary>The memory audit's directory, resolved the way the audit service resolves it.</summary>
    internal static string AuditRoot(MemoryAuditOptions audit, AgentProfileOptions profile)
    {
        if (Path.IsPathRooted(audit.BasePath))
            return audit.BasePath;
        var baseDir = Path.IsPathRooted(profile.BasePath)
            ? profile.BasePath
            : Path.Combine(AppContext.BaseDirectory, profile.BasePath);
        return Path.Combine(baseDir, audit.BasePath);
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
