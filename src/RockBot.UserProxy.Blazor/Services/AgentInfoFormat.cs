using System.Globalization;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// How the agent info dialog (#834) words what the agent reports. Kept out of the component so the
/// rules are testable without rendering it.
/// </summary>
public static class AgentInfoFormat
{
    /// <summary>A rate in USD per million tokens: two decimals, three below $0.10 so small rates don't read as $0.00.</summary>
    public static string Money(double perMillion) =>
        "$" + perMillion.ToString(perMillion is > 0 and < 0.1 ? "0.000" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>A model's price as one line, or the reason it has none.</summary>
    public static string Price(PriceIntrospection? price) =>
        price is null
            ? "No pricing row: costed at $0"
            : $"{Money(price.InputPerM)} in · {Money(price.OutputPerM)} out" +
              (price.CachedInputPerM is { } cached ? $" · {Money(cached)} cached" : "") +
              " per M tokens";

    /// <summary>A cache rate, or what it defaults to when the row leaves it out.</summary>
    public static string CacheRate(double? rate, string fallback) => rate is { } r ? Money(r) : fallback;

    /// <summary>The short facts shown beside a model's id, in order.</summary>
    public static IReadOnlyList<string> Tags(ModelIntrospection model, TierIntrospection tier)
    {
        var tags = new List<string> { model.Service };
        if (model.EndpointHost is { } host && !string.Equals(host, model.Service, StringComparison.OrdinalIgnoreCase))
            tags.Add(host);
        if (model.ReasoningEffort is { } effort)
            tags.Add($"reasoning: {effort}");
        if (tier.AcceptsImages)
            tags.Add(model.SupportsImageInput ? "reads images" : "text only");
        return tags;
    }

    /// <summary>How long the agent has been up: "3d 4h", "5h 12m", "42m", "under a minute".</summary>
    public static string Uptime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1))
            return "under a minute";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m";
        if (elapsed < TimeSpan.FromDays(1))
            return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
        return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h";
    }

    /// <summary>An estimated cost: "$12.34", "<$0.01" for a sliver, "$0.00" for none.</summary>
    public static string Cost(double usd) =>
        usd <= 0 ? "$0.00"
        : usd < 0.01 ? "<$0.01"
        : "$" + usd.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>A token count, compact: 950, 12.3K, 4.56M.</summary>
    public static string Tokens(long count) => count switch
    {
        >= 1_000_000 => (count / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (count / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K",
        _ => count.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>The share of input tokens served from the prompt cache, as "62% cached", or null with no input.</summary>
    public static string? CacheShare(UsageTotalsIntrospection totals) =>
        totals.InputTokens <= 0 ? null
        : $"{(int)Math.Round(totals.CachedInputTokens * 100.0 / totals.InputTokens)}% cached";

    /// <summary>A workload kind in words.</summary>
    public static string KindLabel(string kind) => kind switch
    {
        "session" => "Conversations and scheduled tasks",
        "subagent" => "Subagents",
        "worker" => "Workers",
        "patrol" => "Background (dreams, audits)",
        _ => "Other",
    };

    /// <summary>A <c>yyyy-MM-dd</c> date as "Sun, Nov 15"; the text unchanged when it doesn't parse.</summary>
    public static string Day(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("ddd, MMM d", CultureInfo.InvariantCulture)
            : date;

    /// <summary>
    /// The colour class for a state, always shown beside its words: <c>ok</c> (connected, healthy),
    /// <c>bad</c> (failed, sign-in required, alert), <c>warn</c> (pending, warning), else <c>idle</c>.
    /// </summary>
    public static string StateTone(string state) => state switch
    {
        "connected" or "healthy" => "ok",
        "failed" or "sign-in required" or "alert" => "bad",
        "sign-in pending" or "warning" => "warn",
        _ => "idle",
    };

    /// <summary>An MCP server state in words.</summary>
    public static string McpState(string state) => state switch
    {
        "connected" => "Connected",
        "sign-in required" => "Needs sign-in",
        "sign-in pending" => "Signing in",
        "failed" => "Failed to connect",
        "not connected" => "Not connected",
        _ => state,
    };

    /// <summary>A connected server's tools, prompts and resources in words.</summary>
    public static string McpCounts(McpServerIntrospection server)
    {
        var parts = new List<string> { Count(server.ToolCount, "tool") };
        if (server.PromptCount > 0)
            parts.Add(Count(server.PromptCount, "prompt"));
        if (server.ResourceCount > 0)
            parts.Add(Count(server.ResourceCount, "resource"));
        return string.Join(" · ", parts);
    }

    /// <summary>A memory audit status in words.</summary>
    public static string AuditStatus(string? status) => status switch
    {
        "healthy" => "Healthy",
        "warning" => "Warning",
        "alert" => "Alert",
        null or "" => "Unknown",
        _ => status,
    };

    /// <summary>A 0–1 share as a whole percentage: "73%".</summary>
    public static string Percent(double share) =>
        Math.Round(share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A signed daily change: "+2.5", "-1.0", "0".</summary>
    public static string Growth(double perDay) => perDay.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture);

    /// <summary>Milliseconds as seconds: "4.2s", "850ms" under a second.</summary>
    public static string Seconds(long ms) =>
        ms < 1000 ? $"{ms}ms" : (ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "s";

    /// <summary>How long ago: "just now", "5m ago", "3h ago", "2d ago", "6w ago".</summary>
    public static string Ago(TimeSpan elapsed) =>
        elapsed < TimeSpan.FromMinutes(1) ? "just now"
        : elapsed < TimeSpan.FromHours(1) ? $"{(int)elapsed.TotalMinutes}m ago"
        : elapsed < TimeSpan.FromDays(1) ? $"{(int)elapsed.TotalHours}h ago"
        : elapsed < TimeSpan.FromDays(14) ? $"{(int)elapsed.TotalDays}d ago"
        : $"{(int)(elapsed.TotalDays / 7)}w ago";

    private static string Count(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>A timestamp in the viewer's time zone.</summary>
    public static string When(DateTimeOffset value, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(value, zone).ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture);
}
