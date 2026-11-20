using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace RockBot.Agent.McpBridge.Watches;

/// <summary>
/// The user's standing watch rules (#853): instructions for every watched resource on a server
/// whose URI matches a pattern, so a class of watches (every scheduler poll) needs no per-watch
/// instructions. See <c>design/mcp-bridge.md</c>, "Standing watch rules".
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>The user's, and only the user's.</b> A rule is set only through
///   <c>mcp_set_watch_rule</c> from a turn the user's own message started. The file is the
///   bridge's: it is not a skill, a memory or a rule in the agent's rules store, so nothing that
///   consolidates or rewrites those (the dream) ever reads or changes it.</item>
///   <item><b>Agent-wide.</b> A rule applies to matching watches in every conversation.</item>
///   <item><b>Read at delivery.</b> A changed rule applies to the next change of every matching watch.</item>
/// </list>
/// The file carries a top-level <c>version</c> (<see cref="CurrentVersion"/>) and follows
/// <c>design/schema-migrations.md</c>.
/// </remarks>
internal sealed class McpWatchRuleStore
{
    /// <summary>Version of the rule file's shape.</summary>
    public const int CurrentVersion = 1;

    private readonly JsonLedger<McpWatchRule> _ledger;

    public McpWatchRuleStore(string path, ILogger logger, TimeProvider? time = null) =>
        _ledger = new JsonLedger<McpWatchRule>(path, "MCP watch rule store", CurrentVersion, logger, time);

    /// <summary>Where the rules live.</summary>
    public string Path => _ledger.Path;

    /// <summary>The rules in force, oldest first.</summary>
    public async Task<IReadOnlyList<McpWatchRule>> ActiveAsync(CancellationToken ct = default) =>
        (await _ledger.ReadAllAsync(ct)).Where(r => r.IsActive).OrderBy(r => r.CreatedAt).ToList();

    /// <summary>The rules in force that cover <paramref name="uri"/> on <paramref name="server"/>.</summary>
    public async Task<IReadOnlyList<McpWatchRule>> MatchingAsync(string server, string uri, CancellationToken ct = default) =>
        (await ActiveAsync(ct)).Where(r => r.Matches(server, uri)).ToList();

    /// <summary>
    /// Sets the rule for <paramref name="server"/> and <paramref name="pattern"/>, replacing the one
    /// there is. Returns the rule, and whether it replaced one.
    /// </summary>
    public async Task<(McpWatchRule Rule, bool Replaced)> SetAsync(
        string server, string pattern, string instructions, CancellationToken ct = default)
    {
        var now = _ledger.Time.GetUtcNow();
        if (await FindAsync(server, pattern, ct) is { } existing)
        {
            await _ledger.UpdateAsync(existing.RuleId, r =>
            {
                r.Instructions = instructions;
                r.UpdatedAt = now;
            }, ct);
            existing.Instructions = instructions;
            existing.UpdatedAt = now;
            return (existing, true);
        }

        var rule = new McpWatchRule
        {
            RuleId = "r_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(),
            Server = server,
            UriPattern = pattern,
            Instructions = instructions,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _ledger.AddAsync(rule, ct);
        return (rule, false);
    }

    /// <summary>Removes the rule for <paramref name="server"/> and <paramref name="pattern"/>; null when there was none.</summary>
    public async Task<McpWatchRule?> RemoveAsync(string server, string pattern, CancellationToken ct = default)
    {
        if (await FindAsync(server, pattern, ct) is not { } existing)
            return null;
        var now = _ledger.Time.GetUtcNow();
        await _ledger.UpdateAsync(existing.RuleId, r => r.RemovedAt ??= now, ct);
        return existing;
    }

    private async Task<McpWatchRule?> FindAsync(string server, string pattern, CancellationToken ct) =>
        (await ActiveAsync(ct)).FirstOrDefault(r =>
            string.Equals(r.Server, server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.UriPattern, pattern, StringComparison.Ordinal));
}

/// <summary>One standing watch rule (#853).</summary>
internal sealed class McpWatchRule : ILedgerEntry<McpWatchRule>
{
    /// <summary>The rule's id (<c>r_…</c>), the one the agent sees.</summary>
    public string RuleId { get; set; } = "";

    /// <summary>The server the rule covers, by its configured name.</summary>
    public string Server { get; set; } = "";

    /// <summary>The URIs it covers; <c>*</c> stands for any run of characters other than <c>/</c>.</summary>
    public string UriPattern { get; set; } = "";

    /// <summary>The user's instructions, as they gave them.</summary>
    public string Instructions { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the user removed it; the store purges it a day later.</summary>
    public DateTimeOffset? RemovedAt { get; set; }

    [JsonIgnore]
    public bool IsActive => RemovedAt is null;

    /// <summary>Whether the rule covers <paramref name="uri"/> on <paramref name="server"/>.</summary>
    public bool Matches(string server, string uri) =>
        string.Equals(Server, server, StringComparison.OrdinalIgnoreCase) && UriMatches(UriPattern, uri);

    /// <summary>
    /// Whether <paramref name="uri"/> matches <paramref name="pattern"/>: literal text, with
    /// <c>*</c> for any run of characters other than <c>/</c> (so one path segment, or part of one).
    /// </summary>
    /// <remarks>
    /// No regex: a user's pattern can't make matching slow. As <c>*</c> never matches <c>/</c>, the
    /// pattern and the URI have the same segments, and each segment is matched on its own.
    /// </remarks>
    public static bool UriMatches(string pattern, string uri)
    {
        var patternSegments = pattern.Split('/');
        var uriSegments = uri.Split('/');
        if (patternSegments.Length != uriSegments.Length)
            return false;
        for (var i = 0; i < patternSegments.Length; i++)
        {
            if (!SegmentMatches(patternSegments[i], uriSegments[i]))
                return false;
        }
        return true;
    }

    /// <summary>Wildcard match within one segment (<c>*</c> for any run of characters), in time linear in practice.</summary>
    private static bool SegmentMatches(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, resume = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] != '*' && pattern[p] == text[t])
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++resume;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    /// <summary>The longest pattern a rule may have.</summary>
    public const int MaxPatternChars = 500;

    string ILedgerEntry<McpWatchRule>.Key => RuleId;

    [JsonIgnore]
    bool ILedgerEntry<McpWatchRule>.IsSettled => RemovedAt is not null;

    [JsonIgnore]
    DateTimeOffset? ILedgerEntry<McpWatchRule>.ResolvedAt => RemovedAt;

    public McpWatchRule Clone() =>
        JsonSerializer.Deserialize<McpWatchRule>(JsonSerializer.Serialize(this))!;
}
