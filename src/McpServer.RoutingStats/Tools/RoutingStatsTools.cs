using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace McpServer.RoutingStats.Tools;

[McpServerToolType]
public sealed class RoutingStatsTools(IConfiguration configuration)
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private string LogPath => configuration["RoutingLog:Path"] ?? "/data/agent/tier-routing-log.jsonl";

    [McpServerTool(Name = "get_routing_stats")]
    [Description(
        "Returns aggregate statistics for the LLM tier routing log: counts and percentages " +
        "routed to each tier (low/balanced/high), average complexity scores, latency, token " +
        "usage, and tool call counts. Also shows the context breakdown (user-message vs subagent) " +
        "and fallback count. The log holds up to 200 entries.")]
    public async Task<string> GetRoutingStatsAsync()
    {
        var entries = await ReadEntriesAsync();

        if (entries.Count == 0)
            return JsonSerializer.Serialize(new { message = "No routing log entries found.", logPath = LogPath }, WriteOptions);

        var tiers = new[] { "Low", "Balanced", "High" };
        var tierStats = tiers.ToDictionary(
            t => t.ToLowerInvariant(),
            t =>
            {
                var group = entries.Where(e => string.Equals(e.Tier, t, StringComparison.OrdinalIgnoreCase)).ToList();
                return BuildTierStats(group, entries.Count);
            });

        var userMessageCount = entries.Count(e => e.Context == "user-message");
        var subagentCount = entries.Count(e => e.Context == "subagent");
        var fallbackCount = entries.Count(e => e.IsFallbackTriggered);

        var result = new
        {
            totalEntries = entries.Count,
            logCapacity = 200,
            dataFrom = entries.Min(e => e.Timestamp),
            dataThrough = entries.Max(e => e.Timestamp),
            tiers = tierStats,
            context = new
            {
                userMessage = userMessageCount,
                subagent = subagentCount
            },
            fallbacks = fallbackCount,
            fallbackPct = Math.Round(fallbackCount * 100.0 / entries.Count, 1)
        };

        return JsonSerializer.Serialize(result, WriteOptions);
    }

    [McpServerTool(Name = "get_routing_log")]
    [Description(
        "Returns recent raw entries from the LLM tier routing log. Each entry includes the " +
        "prompt preview, tier assigned, complexity score, matched keywords, token counts, " +
        "latency, tool calls, and whether a model fallback was triggered. " +
        "Useful for spotting individual misroutes or patterns.")]
    public async Task<string> GetRoutingLogAsync(
        [Description("Number of recent entries to return (1–50, default 20).")] int count = 20)
    {
        count = Math.Clamp(count, 1, 50);
        var entries = await ReadEntriesAsync();
        var recent = entries.TakeLast(count).ToList();

        var result = new
        {
            totalInLog = entries.Count,
            returned = recent.Count,
            entries = recent
        };

        return JsonSerializer.Serialize(result, WriteOptions);
    }

    private async Task<List<RoutingEntry>> ReadEntriesAsync()
    {
        if (!File.Exists(LogPath))
            return [];

        try
        {
            var lines = await File.ReadAllLinesAsync(LogPath);
            var entries = new List<RoutingEntry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<RoutingEntry>(line, ReadOptions);
                    if (entry is not null)
                        entries.Add(entry);
                }
                catch (JsonException) { /* skip malformed lines */ }
            }
            return entries;
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static object BuildTierStats(List<RoutingEntry> group, int total)
    {
        var count = group.Count;
        var pct = total > 0 ? Math.Round(count * 100.0 / total, 1) : 0.0;

        var withLatency = group.Where(e => e.LatencyMs.HasValue).ToList();
        var withInputTokens = group.Where(e => e.InputTokens.HasValue).ToList();
        var withOutputTokens = group.Where(e => e.OutputTokens.HasValue).ToList();
        var withToolCalls = group.Where(e => e.ToolCallCount.HasValue).ToList();

        return new
        {
            count,
            pct,
            avgComplexityScore = count > 0 ? Math.Round(group.Average(e => e.ComplexityScore), 3) : 0.0,
            avgLatencyMs = withLatency.Count > 0 ? (long?)Math.Round(withLatency.Average(e => e.LatencyMs!.Value)) : null,
            avgInputTokens = withInputTokens.Count > 0 ? (long?)Math.Round(withInputTokens.Average(e => e.InputTokens!.Value)) : null,
            avgOutputTokens = withOutputTokens.Count > 0 ? (long?)Math.Round(withOutputTokens.Average(e => e.OutputTokens!.Value)) : null,
            avgToolCalls = withToolCalls.Count > 0 ? Math.Round(withToolCalls.Average(e => e.ToolCallCount!.Value), 2) : 0.0
        };
    }
}

// Local mirror of TierRoutingEntry — matches the camelCase JSONL written by TierRoutingLogger.
internal sealed record RoutingEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string PromptPreview { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Context { get; init; } = "";
    public double ComplexityScore { get; init; }
    public List<string> MatchedHighKeywords { get; init; } = [];
    public List<string> MatchedLowKeywords { get; init; } = [];
    public int? PostInjectionTokenEstimate { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? LatencyMs { get; init; }
    public int? ToolCallCount { get; init; }
    public List<string>? ToolsUsed { get; init; }
    public bool IsFallbackTriggered { get; init; }
}
