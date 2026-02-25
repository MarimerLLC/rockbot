using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Memory;

/// <summary>
/// LLM-callable tools for working memory — a global, path-namespaced scratch space shared
/// by all execution contexts (user sessions, patrol tasks, subagents).
///
/// Constructed with a <paramref name="@namespace"/> prefix (e.g. <c>session/abc123</c>,
/// <c>patrol/heartbeat</c>, <c>subagent/task1</c>) that is automatically prepended to
/// keys on write, providing namespace isolation without restricting cross-context reads.
/// </summary>
public sealed class WorkingMemoryTools
{
    private readonly IWorkingMemory _workingMemory;
    private readonly string _namespace;
    private readonly ILogger _logger;

    public WorkingMemoryTools(IWorkingMemory workingMemory, string @namespace, ILogger logger)
    {
        _workingMemory = workingMemory;
        _namespace = @namespace;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(SaveToWorkingMemory),
            AIFunctionFactory.Create(GetFromWorkingMemory),
            AIFunctionFactory.Create(DeleteFromWorkingMemory),
            AIFunctionFactory.Create(ListWorkingMemory),
            AIFunctionFactory.Create(SearchWorkingMemory)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description("Cache data in working memory so it can be retrieved in follow-up questions without re-fetching. " +
                 "Data is stored under your namespace automatically — just provide a descriptive key. " +
                 "Use this after receiving a large payload from any tool, or to store intermediate results " +
                 "that a subagent or patrol task should leave for the primary agent to pick up. " +
                 "Assign a category and tags to make the data easier to find with search_working_memory.")]
    public async Task<string> SaveToWorkingMemory(
        [Description("Short descriptive key (e.g. 'emails_inbox', 'research_results', 'patrol_alert')")] string key,
        [Description("The data to cache — can be a large string, JSON payload, or formatted summary")] string data,
        [Description("How long to keep this data in minutes (default: 5). " +
                     "Use longer TTLs for subagent outputs (e.g. 240) or patrol state (e.g. 300).")] int? ttl_minutes = null,
        [Description("Optional category for grouping related entries (e.g. 'email', 'calendar', 'research/pricing')")] string? category = null,
        [Description("Optional comma-separated tags for filtering (e.g. 'inbox,unread,urgent')")] string? tags = null)
    {
        var fullKey = $"{_namespace}/{key}";
        _logger.LogInformation("Tool call: SaveToWorkingMemory(key={Key}, ttl={Ttl}min, category={Category})", fullKey, ttl_minutes, category);
        var ttl = ttl_minutes.HasValue ? TimeSpan.FromMinutes(ttl_minutes.Value) : (TimeSpan?)null;
        var tagList = ParseTags(tags);
        await _workingMemory.SetAsync(fullKey, data, ttl, category, tagList);
        return $"Saved to working memory under key '{fullKey}'.";
    }

    [Description("Retrieve previously cached data from working memory by key. " +
                 "Use a plain key (e.g. 'emails_inbox') to retrieve from your own namespace, " +
                 "or a full path (e.g. 'subagent/task1/results') to read from another namespace.")]
    public async Task<string> GetFromWorkingMemory(
        [Description("Key to retrieve — plain key for own namespace, full path for cross-namespace (e.g. 'subagent/task1/results')")] string key)
    {
        _logger.LogInformation("Tool call: GetFromWorkingMemory(key={Key})", key);
        // If the key contains '/', treat as an absolute path; otherwise prepend namespace.
        var fullKey = key.Contains('/') ? key : $"{_namespace}/{key}";
        var value = await _workingMemory.GetAsync(fullKey);
        if (value is null)
            return $"Working memory entry '{fullKey}' not found or has expired.";
        return value;
    }

    [Description("Delete an entry from working memory by key. " +
                 "Use this to dismiss resolved patrol findings, clear stale data, or remove entries that are no longer needed. " +
                 "Use a plain key to delete from your own namespace, or a full path (e.g. 'patrol/heartbeat-patrol/...') to delete from another namespace.")]
    public async Task<string> DeleteFromWorkingMemory(
        [Description("Key to delete — plain key for own namespace, full path for cross-namespace (e.g. 'patrol/heartbeat-patrol/critical-actions-required')")] string key)
    {
        var fullKey = key.Contains('/') ? key : $"{_namespace}/{key}";
        _logger.LogInformation("Tool call: DeleteFromWorkingMemory(key={Key})", fullKey);
        await _workingMemory.DeleteAsync(fullKey);
        return $"Working memory entry '{fullKey}' deleted.";
    }

    [Description("List all keys currently in working memory with their category, tags, and expiry times. " +
                 "Defaults to your own namespace. Pass a namespace prefix to browse another context — " +
                 "for example 'subagent/task1' to see what a completed subagent stored, " +
                 "or 'patrol' to see all patrol task outputs.")]
    public async Task<string> ListWorkingMemory(
        [Description("Optional namespace prefix to browse (e.g. 'subagent/task1', 'patrol'). Omit to list your own namespace.")] string? @namespace = null)
    {
        var prefix = string.IsNullOrWhiteSpace(@namespace) ? _namespace : @namespace.Trim();
        _logger.LogInformation("Tool call: ListWorkingMemory(prefix={Prefix})", prefix);
        var entries = await _workingMemory.ListAsync(prefix);

        if (entries.Count == 0)
            return prefix == _namespace
                ? "Working memory is empty."
                : $"No entries found in namespace '{prefix}'.";

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"Working memory '{prefix}' ({entries.Count} entries):");
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
                 "Results are ranked by BM25 relevance. Defaults to your own namespace. " +
                 "Pass a namespace prefix to search another context.")]
    public async Task<string> SearchWorkingMemory(
        [Description("Keywords to search for in cached content. Omit to list all entries in the namespace/category/tag scope.")] string? query = null,
        [Description("Optional category prefix to filter by (e.g. 'research', 'email')")] string? category = null,
        [Description("Optional comma-separated tags that entries must have (e.g. 'urgent,inbox')")] string? tags = null,
        [Description("Optional namespace prefix to search (e.g. 'subagent/task1', 'patrol'). Omit to search your own namespace.")] string? @namespace = null)
    {
        var prefix = string.IsNullOrWhiteSpace(@namespace) ? _namespace : @namespace.Trim();
        _logger.LogInformation("Tool call: SearchWorkingMemory(query={Query}, category={Category}, prefix={Prefix})", query, category, prefix);

        var criteria = new MemorySearchCriteria(
            Query: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            Tags: ParseTags(tags));

        var entries = await _workingMemory.SearchAsync(criteria, prefix);

        if (entries.Count == 0)
        {
            var desc = BuildSearchDesc(query, category, tags);
            return $"No working memory entries matched {desc} in namespace '{prefix}'.";
        }

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        var desc2 = BuildSearchDesc(query, category, tags);
        sb.AppendLine($"Working memory search {desc2} in '{prefix}' — {entries.Count} result(s):");
        foreach (var entry in entries)
        {
            var remaining = entry.ExpiresAt - now;
            var remainingStr = remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                : $"{Math.Max(0, remaining.Seconds)}s";
            var preview = entry.Value.Length > 120 ? entry.Value[..120] + "\u2026" : entry.Value;
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
