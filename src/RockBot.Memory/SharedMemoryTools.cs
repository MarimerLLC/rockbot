using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.Memory;

/// <summary>
/// LLM-callable tools for cross-session shared memory — a scratch space accessible to all
/// execution contexts (user sessions, patrol tasks, subagents). Unlike <see cref="WorkingMemoryTools"/>,
/// no session ID is needed — keys are global.
///
/// Registered as a singleton; no per-session baking required.
/// </summary>
public sealed class SharedMemoryTools
{
    private readonly ISharedMemory _sharedMemory;
    private readonly ILogger _logger;

    public SharedMemoryTools(ISharedMemory sharedMemory, ILogger<SharedMemoryTools> logger)
    {
        _sharedMemory = sharedMemory;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(SaveToSharedMemory),
            AIFunctionFactory.Create(GetFromSharedMemory),
            AIFunctionFactory.Create(ListSharedMemory),
            AIFunctionFactory.Create(SearchSharedMemory)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description("Save data to shared memory — a cross-session scratch space shared by all sessions, " +
                 "patrol tasks, and subagents. Use this for data that needs to be exchanged between " +
                 "different execution contexts. Entries expire automatically based on TTL. " +
                 "Choose a descriptive key and assign a category and tags for discoverability.")]
    public async Task<string> SaveToSharedMemory(
        [Description("Short descriptive key (e.g. 'patrol-alert-2026-02-23', 'research-output')")] string key,
        [Description("The data to store — can be a large string, JSON payload, or formatted summary")] string data,
        [Description("How long to keep this data in minutes (default: 30)")] int? ttl_minutes = null,
        [Description("Optional category for grouping related entries (e.g. 'subagent-output', 'patrol-finding', 'exchange')")] string? category = null,
        [Description("Optional comma-separated tags for filtering (e.g. 'urgent,email-report')")] string? tags = null)
    {
        _logger.LogInformation("Tool call: SaveToSharedMemory(key={Key}, ttl={Ttl}min, category={Category})", key, ttl_minutes, category);
        var ttl = ttl_minutes.HasValue ? TimeSpan.FromMinutes(ttl_minutes.Value) : (TimeSpan?)null;
        var tagList = ParseTags(tags);
        await _sharedMemory.SetAsync(key, data, ttl, category, tagList);
        return $"Saved to shared memory under key '{key}'.";
    }

    [Description("Retrieve data from shared memory by key. Shared memory is accessible across all sessions — " +
                 "use when the context shows a shared memory entry or another session/subagent stored data for you.")]
    public async Task<string> GetFromSharedMemory(
        [Description("The key to retrieve (as shown in the shared memory list)")] string key)
    {
        _logger.LogInformation("Tool call: GetFromSharedMemory(key={Key})", key);
        var value = await _sharedMemory.GetAsync(key);
        if (value is null)
            return $"Shared memory entry '{key}' not found or has expired.";
        return value;
    }

    [Description("List all keys currently in shared memory with their category, tags, and expiry times. " +
                 "Shared memory is a cross-session scratch space — entries may have been written by " +
                 "patrol tasks, subagents, or other sessions.")]
    public async Task<string> ListSharedMemory()
    {
        _logger.LogInformation("Tool call: ListSharedMemory()");
        var entries = await _sharedMemory.ListAsync();

        if (entries.Count == 0)
            return "Shared memory is empty.";

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.AppendLine($"Shared memory ({entries.Count} entries):");
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

    [Description("Search shared memory by keyword, category, and/or tags. " +
                 "Results are ranked by BM25 relevance. Shared memory is cross-session — " +
                 "use this to find data left by patrol tasks, subagents, or other sessions.")]
    public async Task<string> SearchSharedMemory(
        [Description("Keywords to search for (e.g. 'email report', 'patrol alert'). Omit to list all entries in the category/tag scope.")] string? query = null,
        [Description("Optional category prefix to filter by (e.g. 'subagent-output', 'patrol-finding')")] string? category = null,
        [Description("Optional comma-separated tags that entries must have (e.g. 'urgent,report')")] string? tags = null)
    {
        _logger.LogInformation("Tool call: SearchSharedMemory(query={Query}, category={Category}, tags={Tags})", query, category, tags);

        var criteria = new MemorySearchCriteria(
            Query: string.IsNullOrWhiteSpace(query) ? null : query.Trim(),
            Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            Tags: ParseTags(tags));

        var entries = await _sharedMemory.SearchAsync(criteria);

        if (entries.Count == 0)
        {
            var desc = BuildSearchDesc(query, category, tags);
            return $"No shared memory entries matched {desc}.";
        }

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        var desc2 = BuildSearchDesc(query, category, tags);
        sb.AppendLine($"Shared memory search {desc2} — {entries.Count} result(s):");
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
