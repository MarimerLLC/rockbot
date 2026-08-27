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
            AIFunctionFactory.Create(EditWorkingMemory),
            AIFunctionFactory.Create(DeleteFromWorkingMemory),
            AIFunctionFactory.Create(SearchWorkingMemory)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description("EPHEMERAL cache for this conversation — entries expire on a TTL measured in minutes and " +
                 "do NOT survive a restart. Use it for payloads (tool results, drafts, intermediate data), " +
                 "not for facts or preferences worth keeping; for those use save_memory instead. " +
                 "Cache data here so it can be retrieved in follow-up questions without re-fetching. " +
                 "Data is stored under your namespace automatically — just provide a descriptive key. " +
                 "Use this after receiving a large payload from any tool, or to store intermediate results " +
                 "that a subagent or patrol task should leave for the primary agent to pick up. " +
                 "To hand data off to a different session, patrol, or subagent, pass a full-path key beginning " +
                 "with 'shared/' (e.g. 'shared/drafts/tina-vslive') — entries under shared/ are auto-listed in " +
                 "every context. Assign a category and tags to make the data easier to find with search_working_memory.")]
    public async Task<string> SaveToWorkingMemory(
        [Description("Short descriptive key (e.g. 'emails_inbox', 'research_results', 'patrol_alert')")] string key,
        [Description("The data to cache — can be a large string, JSON payload, or formatted summary")] string data,
        [Description("How long to keep this data in minutes (default: 5). " +
                     "Use longer TTLs for subagent outputs (e.g. 240) or patrol state (e.g. 300).")] int? ttl_minutes = null,
        [Description("Optional category for grouping related entries (e.g. 'email', 'calendar', 'research/pricing')")] string? category = null,
        [Description("Optional comma-separated tags for filtering (e.g. 'inbox,unread,urgent')")] string? tags = null)
    {
        // If the key contains '/', treat as an absolute path; otherwise prepend namespace.
        var fullKey = key.Contains('/') ? key : $"{_namespace}/{key}";
        _logger.LogInformation("Tool call: SaveToWorkingMemory(key={Key}, ttl={Ttl}min, category={Category})", fullKey, ttl_minutes, category);
        var ttl = ttl_minutes.HasValue ? TimeSpan.FromMinutes(ttl_minutes.Value) : (TimeSpan?)null;
        var tagList = ParseTags(tags);

        // Phase 2 soft gate — capability-claim language is flagged as an observation
        // (write proceeds; tag added so the dream service can later evaluate promotion).
        var (gatedTags, hint) = ObservationLanguageDetector.ApplySoftGate(data, tagList);

        await _workingMemory.SetAsync(fullKey, data, ttl, category, gatedTags);
        return $"Saved to working memory under key '{fullKey}'.{hint}";
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

    [Description("Change part of a cached working memory entry without re-sending the whole payload. " +
                 "Use this to amend a running draft, checklist, or handoff note in place — re-saving it with " +
                 "save_to_working_memory means reproducing every character you want to keep, and anything you " +
                 "do not reproduce is gone. " +
                 "old_string must match the cached value exactly; if it appears more than once the edit is " +
                 "refused, so include more surrounding text or set replace_all. " +
                 "The entry keeps its category and tags, and its TTL restarts from now.")]
    public async Task<string> EditWorkingMemory(
        [Description("Key to edit — plain key for own namespace, full path for cross-namespace (e.g. 'shared/drafts/report')")] string key,
        [Description("Exact text to find inside the cached value — copy it verbatim")] string old_string,
        [Description("Replacement text. Pass an empty string to delete the matched text.")] string new_string,
        [Description("Replace every occurrence instead of refusing an ambiguous match. Default false.")] bool replace_all = false)
    {
        var fullKey = key.Contains('/') ? key : $"{_namespace}/{key}";
        _logger.LogInformation("Tool call: EditWorkingMemory(key={Key}, replaceAll={ReplaceAll})", fullKey, replace_all);

        var result = await _workingMemory.EditAsync(
            fullKey, old_string ?? string.Empty, new_string ?? string.Empty, replace_all);

        if (!result.IsSuccess)
            return $"Edit failed on working memory entry '{fullKey}': {result.Error}";

        var plural = result.ReplacementCount == 1 ? "occurrence" : "occurrences";
        return $"Edited working memory entry '{fullKey}' — replaced {result.ReplacementCount} {plural} " +
               $"({result.OldLength} → {result.NewLength} characters). TTL restarted.";
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

    /// <summary>
    /// Result cap for the no-query listing path. <see cref="MemorySearchCriteria"/> defaults to
    /// 20 results, which is right for a ranked search but would silently truncate a namespace
    /// listing — the behaviour the folded-in <c>list_working_memory</c> tool had to preserve.
    /// High enough to be effectively unbounded for real namespaces, bounded so a runaway
    /// namespace cannot blow the context window on its own.
    /// </summary>
    private const int ListingMaxResults = 500;

    [Description($"{RecallTools.WorkingHeadline} — search or list THIS SESSION'S ephemeral cached " +
                 "payloads: tool results, subagent output, patrol findings, and cross-context handoffs " +
                 "under 'shared/'. Entries expire on a TTL measured in minutes. " +
                 "Omit query to LIST everything in scope (key, category, tags, expiry — no content preview). " +
                 "Supply query to rank cached content by relevance. Filter with category and/or tags. " +
                 "Defaults to your own namespace; pass a namespace prefix to browse another context — " +
                 "'subagent/task1' for what a completed subagent stored, 'patrol' for all patrol task " +
                 "outputs, 'shared' for the cross-session handoff namespace. " +
                 $"Sibling recall tool — {RecallTools.TryDurable}.")]
    public async Task<string> SearchWorkingMemory(
        [Description("Keywords to search for in cached content. Omit to list all entries in the namespace/category/tag scope.")] string? query = null,
        [Description("Optional category prefix to filter by (e.g. 'research', 'email')")] string? category = null,
        [Description("Optional comma-separated tags that entries must have (e.g. 'urgent,inbox')")] string? tags = null,
        [Description("Optional namespace prefix to search (e.g. 'subagent/task1', 'patrol'). Omit to search your own namespace.")] string? @namespace = null)
    {
        var prefix = string.IsNullOrWhiteSpace(@namespace) ? _namespace : @namespace.Trim();
        var trimmedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        // No query means "browse this scope" rather than "rank by relevance" — the surface the
        // folded-in list_working_memory tool used to provide. Listings render metadata only, and
        // raise the result cap so browsing a namespace is not silently truncated at the ranked
        // search default.
        var isListing = trimmedQuery is null;

        _logger.LogInformation(
            "Tool call: SearchWorkingMemory(query={Query}, category={Category}, prefix={Prefix}, listing={Listing})",
            query, category, prefix, isListing);

        var criteria = new MemorySearchCriteria(
            Query: trimmedQuery,
            Category: string.IsNullOrWhiteSpace(category) ? null : category.Trim(),
            Tags: ParseTags(tags));

        if (isListing)
            criteria = criteria with { MaxResults = ListingMaxResults };

        var entries = await _workingMemory.SearchAsync(criteria, prefix);

        if (entries.Count == 0)
        {
            // An unfiltered listing over an empty scope isn't a failed search — keep the
            // plain-language wording the list tool used.
            if (isListing && criteria.Category is null && criteria.Tags is null)
                return prefix == _namespace
                    ? "Working memory is empty."
                    : $"No entries found in namespace '{prefix}'.";

            var desc = BuildSearchDesc(query, category, tags);
            return $"No working memory entries matched {desc} in namespace '{prefix}'. " +
                   RecallTools.LookElsewhere(RecallTools.WorkingMemory);
        }

        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        var desc2 = BuildSearchDesc(query, category, tags);
        sb.AppendLine(isListing
            ? $"Working memory '{prefix}' ({entries.Count} entries):"
            : $"Working memory search {desc2} in '{prefix}' — {entries.Count} result(s):");
        foreach (var entry in entries)
        {
            var remaining = entry.ExpiresAt - now;
            var remainingStr = remaining.TotalMinutes >= 1
                ? $"{(int)remaining.TotalMinutes}m{remaining.Seconds:D2}s"
                : $"{Math.Max(0, remaining.Seconds)}s";
            sb.Append($"- {entry.Key} (expires in {remainingStr}");
            if (entry.Category is not null) sb.Append($", category: {entry.Category}");
            if (entry.Tags is { Count: > 0 }) sb.Append($", tags: {string.Join(", ", entry.Tags)}");

            if (isListing)
            {
                // Listing mode reproduces the old list_working_memory surface: metadata only,
                // no content preview, so browsing a namespace stays compact.
                sb.AppendLine(")");
            }
            else
            {
                var preview = entry.Value.Length > 120 ? entry.Value[..120] + "\u2026" : entry.Value;
                sb.AppendLine($"): {preview}");
            }
        }

        if (isListing && entries.Count >= ListingMaxResults)
            sb.AppendLine($"(listing capped at {ListingMaxResults} entries \u2014 narrow the scope with namespace, category, or tags)");

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
