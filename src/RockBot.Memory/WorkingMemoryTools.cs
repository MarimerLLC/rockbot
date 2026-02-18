using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Memory;

/// <summary>
/// LLM-callable tools for session-scoped working memory — a scratch space for caching
/// large or expensive-to-fetch tool results so they can be referenced in follow-up turns
/// without re-calling the external source.
///
/// Instantiated per-message with the session ID baked in so no ambient session state is needed.
/// </summary>
public sealed class WorkingMemoryTools
{
    private readonly IWorkingMemory _workingMemory;
    private readonly string _sessionId;
    private readonly ILogger _logger;

    public WorkingMemoryTools(IWorkingMemory workingMemory, string sessionId, ILogger logger)
    {
        _workingMemory = workingMemory;
        _sessionId = sessionId;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(SaveToWorkingMemory),
            AIFunctionFactory.Create(GetFromWorkingMemory),
            AIFunctionFactory.Create(ListWorkingMemory),
            AIFunctionFactory.Create(SearchWorkingMemory)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description("Cache data in working memory (session scratch space) so it can be retrieved " +
                 "in follow-up questions without re-fetching from the external source. " +
                 "Use this after receiving a large payload from any tool to save it temporarily. " +
                 "Choose a descriptive key that summarises what is stored. " +
                 "Assign a category and tags to make the data easier to find with SearchWorkingMemory.")]
    public async Task<string> SaveToWorkingMemory(
        [Description("Short descriptive key (e.g. 'calendar_2026-02-18', 'emails_inbox')")] string key,
        [Description("The data to cache — can be a large string, JSON payload, or formatted summary")] string data,
        [Description("How long to keep this data in minutes (default: 5)")] int? ttl_minutes = null,
        [Description("Optional category for grouping related entries (e.g. 'email', 'calendar', 'research/pricing')")] string? category = null,
        [Description("Optional comma-separated tags for filtering (e.g. 'inbox,unread,urgent')")] string? tags = null)
    {
        _logger.LogInformation("Tool call: SaveToWorkingMemory(key={Key}, ttl={Ttl}min, category={Category})", key, ttl_minutes, category);
        var ttl = ttl_minutes.HasValue ? TimeSpan.FromMinutes(ttl_minutes.Value) : (TimeSpan?)null;
        var tagList = ParseTags(tags);
        await _workingMemory.SetAsync(_sessionId, key, data, ttl, category, tagList);
        return $"Saved to working memory under key '{key}'.";
    }

    [Description("Retrieve previously cached data from working memory by key. " +
                 "Use when the system context shows an entry in working memory that contains relevant data.")]
    public async Task<string> GetFromWorkingMemory(
        [Description("The key to retrieve (as shown in the working memory list)")] string key)
    {
        _logger.LogInformation("Tool call: GetFromWorkingMemory(key={Key})", key);
        var value = await _workingMemory.GetAsync(_sessionId, key);
        if (value is null)
            return $"Working memory entry '{key}' not found or has expired.";
        return value;
    }

    [Description("List all keys currently in working memory with their category, tags, and expiry times. " +
                 "Use this to discover what cached data is available from earlier in this session.")]
    public async Task<string> ListWorkingMemory()
    {
        _logger.LogInformation("Tool call: ListWorkingMemory()");
        var entries = await _workingMemory.ListAsync(_sessionId);

        if (entries.Count == 0)
            return "Working memory is empty.";

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"Working memory ({entries.Count} entries):");
        foreach (var entry in entries)
        {
            var remaining = entry.ExpiresAt - now;
            var remainingStr = remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                : $"{Math.Max(0, remaining.Seconds)}s";

            sb.Append($"- {entry.Key} (expires in {remainingStr}");
            if (entry.Category is not null) sb.Append($", category: {entry.Category}");
            if (entry.Tags is { Count: > 0 }) sb.Append($", tags: {string.Join(", ", entry.Tags)}");
            sb.AppendLine(")");
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Search working memory by keyword, category, and/or tags. " +
                 "Results are ranked by BM25 relevance — the most on-topic entries appear first. " +
                 "Use this when you need to find cached data but do not know the exact key, " +
                 "or when you want to filter entries by topic area.")]
    public async Task<string> SearchWorkingMemory(
        [Description("Keywords to search for in cached content (e.g. 'pricing strategies', 'inbox emails'). Omit to list all entries in the category/tag scope.")] string? query = null,
        [Description("Optional category prefix to filter by (e.g. 'research', 'email')")] string? category = null,
        [Description("Optional comma-separated tags that entries must have (e.g. 'urgent,inbox')")] string? tags = null)
    {
        _logger.LogInformation("Tool call: SearchWorkingMemory(query={Query}, category={Category}, tags={Tags})", query, category, tags);

        var criteria = new MemorySearchCriteria(
            Query: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            Tags: ParseTags(tags));

        var entries = await _workingMemory.SearchAsync(_sessionId, criteria);

        if (entries.Count == 0)
        {
            var desc = BuildSearchDesc(query, category, tags);
            return $"No working memory entries matched {desc}.";
        }

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        var desc2 = BuildSearchDesc(query, category, tags);
        sb.AppendLine($"Working memory search {desc2} — {entries.Count} result(s):");
        foreach (var entry in entries)
        {
            var remaining = entry.ExpiresAt - now;
            var remainingStr = remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                : $"{Math.Max(0, remaining.Seconds)}s";
            var preview = entry.Value.Length > 120 ? entry.Value[..120] + "…" : entry.Value;
            sb.Append($"- {entry.Key} (expires in {remainingStr}");
            if (entry.Category is not null) sb.Append($", category: {entry.Category}");
            if (entry.Tags is { Count: > 0 }) sb.Append($", tags: {string.Join(", ", entry.Tags)}");
            sb.AppendLine($"): {preview}");
        }
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<string>? ParseTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? null
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildSearchDesc(string? query, string? category, string? tags)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parts.Add($"query='{query}'");
        if (!string.IsNullOrWhiteSpace(category)) parts.Add($"category='{category}'");
        if (!string.IsNullOrWhiteSpace(tags)) parts.Add($"tags='{tags}'");
        return parts.Count > 0 ? $"({string.Join(", ", parts)})" : "(no filters)";
    }
}
