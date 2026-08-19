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

    [Description("Search or list THIS SESSION'S ephemeral cached payloads — tool results, subagent " +
                 "output, patrol findings, and cross-context handoffs under 'shared/'. Entries expire " +
                 "on a TTL measured in minutes. " +
                 "Omit query to LIST everything in scope (key, category, tags, expiry — no content preview). " +
                 "Supply query to rank cached content by relevance. Filter with category and/or tags. " +
                 "Defaults to your own namespace; pass a namespace prefix to browse another context — " +
                 "'subagent/task1' for what a completed subagent stored, 'patrol' for all patrol task " +
                 "outputs, 'shared' for the cross-session handoff namespace, and 'stash' for the full " +
                 "untrimmed originals of tool results that were elided from your context earlier in this " +
                 "session (the [stash-registry] message only lists elisions from the current run — 'stash' " +
                 "reaches the earlier ones too). " +
                 "For durable facts and preferences that survive restarts, use search_memory instead. " +
                 "For what was actually said in turns that have scrolled out of your context window, use " +
                 "search_conversation_history instead.")]
    public async Task<string> SearchWorkingMemory(
        [Description("Keywords to search for in cached content. Omit to list all entries in the namespace/category/tag scope.")] string? query = null,
        [Description("Optional category prefix to filter by (e.g. 'research', 'email')")] string? category = null,
        [Description("Optional comma-separated tags that entries must have (e.g. 'urgent,inbox')")] string? tags = null,
        [Description("Optional namespace prefix to search (e.g. 'subagent/task1', 'patrol', 'stash'). Omit to search your own namespace.")] string? @namespace = null)
    {
        var prefix = ResolveNamespace(@namespace);
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
            return $"No working memory entries matched {desc} in namespace '{prefix}'.";
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

    /// <summary>
    /// Resolves the caller-supplied namespace to a working-memory key prefix.
    /// </summary>
    /// <remarks>
    /// Bare <c>stash</c> (or <c>stash/</c>) is an alias for <b>this context's own</b> stash,
    /// which lives at <c>stash/{namespace}</c> — see <c>AgentLoopRunner.BuildStashKey</c>.
    /// The alias exists because the model has no way to learn its own namespace, so without it
    /// the only reachable stash prefix would be the bare <c>stash</c> root shared by every
    /// context. Longer explicit paths (<c>stash/session/other</c>) pass through untouched so
    /// deliberate cross-context reads still work.
    /// </remarks>
    private string ResolveNamespace(string? @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace)) return _namespace;

        var trimmed = @namespace.Trim();
        if (trimmed.Equals("stash", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("stash/", StringComparison.OrdinalIgnoreCase))
        {
            return $"stash/{_namespace}";
        }

        return trimmed;
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
