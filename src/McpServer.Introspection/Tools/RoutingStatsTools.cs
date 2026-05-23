using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RockBot.Host;

namespace McpServer.Introspection.Tools;

/// <summary>
/// MCP-facing introspection tools over the agent's tier-routing log.
/// <para>
/// All three tools read the same JSONL file written by the agent host's
/// <c>TierRoutingLogger</c>. The summary tool delegates aggregation to
/// <see cref="TierRoutingAnalyzer"/> so the agent receives a deterministic
/// digest instead of having to crunch raw entries in-context.
/// </para>
/// </summary>
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
    private string PricingPath => configuration["LlmPricing:Path"] ?? "/data/agent/llm-pricing.json";
    private string TierSelectorPath => configuration["TierSelector:Path"] ?? "/data/agent/tier-selector.json";

    [McpServerTool(Name = "get_routing_summary")]
    [Description(
        "Returns a pre-aggregated analysis of the LLM tier-routing log for the last N hours. " +
        "The server does the statistical heavy lifting: cluster grouping by prompt-shape, " +
        "deterministic detection of misroute patterns (panicEscalation, tokenSurprise, " +
        "lowOutputAtHigh), keyword candidate analysis, threshold scans (\"what if\" projections), " +
        "and per-tier USD cost from the pricing table. Use this for daily routing reports — " +
        "one tool call replaces what would otherwise be raw-log crunching by the LLM. " +
        "windowHours is clamped to 1..168 (one week).")]
    public async Task<string> GetRoutingSummaryAsync(
        [Description("Time window in hours (1..168, default 24).")] int windowHours = 24)
    {
        windowHours = Math.Clamp(windowHours, 1, 168);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-windowHours);

        var entries = await ReadEntriesAsync();
        var windowed = entries.Where(e => e.Timestamp >= cutoff).ToList();

        var currentConfig = await TryReadTierSelectorAsync();
        var pricing = await TryReadPricingAsync();

        var analysis = TierRoutingAnalyzer.Analyze(windowed, currentConfig, pricing);
        return JsonSerializer.Serialize(analysis, WriteOptions);
    }

    [McpServerTool(Name = "get_routing_log")]
    [Description(
        "Returns recent raw entries from the LLM tier-routing log. Use this for spotting " +
        "INDIVIDUAL misroutes — for aggregate reporting prefer get_routing_summary, which " +
        "does the statistical work server-side. Each entry includes the prompt preview, tier, " +
        "complexity score, matched keywords, token counts, latency, tool calls, model ID, and " +
        "whether a model fallback was triggered. count is clamped to 1..50.")]
    public async Task<string> GetRoutingLogAsync(
        [Description("Number of recent entries to return (1..50, default 20).")] int count = 20)
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

    [McpServerTool(Name = "get_routing_stats")]
    [Description(
        "Back-compat wrapper: returns the globalStats section of the routing analysis across " +
        "ALL entries in the log (no time window). Prefer get_routing_summary for time-windowed " +
        "reporting — it returns the same data plus clusters, flagged misroutes, keyword " +
        "candidates, and threshold scans.")]
    public async Task<string> GetRoutingStatsAsync()
    {
        var entries = await ReadEntriesAsync();
        if (entries.Count == 0)
            return JsonSerializer.Serialize(new { message = "No routing log entries found.", logPath = LogPath }, WriteOptions);

        var analysis = TierRoutingAnalyzer.Analyze(entries);
        var compact = new
        {
            totalEntries = analysis.TotalEntries,
            dataFrom = analysis.WindowStart,
            dataThrough = analysis.WindowEnd,
            globalStats = analysis.GlobalStats,
            fallbackExcluded = analysis.FallbackExcludedCount
        };

        return JsonSerializer.Serialize(compact, WriteOptions);
    }

    // ── File reading ─────────────────────────────────────────────────────────

    private async Task<List<TierRoutingEntry>> ReadEntriesAsync()
    {
        if (!File.Exists(LogPath)) return [];

        try
        {
            var lines = await File.ReadAllLinesAsync(LogPath);
            var entries = new List<TierRoutingEntry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<TierRoutingEntry>(line, ReadOptions);
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

    private async Task<TierSelectorConfig?> TryReadTierSelectorAsync()
    {
        if (!File.Exists(TierSelectorPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(TierSelectorPath);
            return JsonSerializer.Deserialize<TierSelectorConfig>(json, ReadOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<LlmPricingRow>?> TryReadPricingAsync()
    {
        if (!File.Exists(PricingPath)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(PricingPath);
            return JsonSerializer.Deserialize<List<LlmPricingRow>>(json, ReadOptions);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
