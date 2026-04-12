using RockBot.A2A;
using RockBot.Host;

namespace RockBot.Agent.A2A;

/// <summary>
/// Fuzzy-matches an inbound A2A skill ID against registered skills using BM25.
/// The A2A protocol is language-based — callers may paraphrase skill IDs
/// (e.g. "schedule-meeting" instead of "negotiate-meeting"). This matcher
/// finds the best registered skill for a given request, falling back to
/// <c>null</c> when no match is confident enough.
/// </summary>
internal static class InboundSkillMatcher
{
    /// <summary>
    /// Minimum BM25 score ratio (relative to the top result) for a match to be
    /// considered confident. Not used when there's only one candidate.
    /// </summary>
    private const double MinScoreRatio = 0.3;

    /// <summary>
    /// Known built-in skill definitions. Each entry contains the canonical ID,
    /// display name, description, and any known aliases.
    /// </summary>
    private static readonly SkillDefinition[] BuiltInSkills =
    [
        new("notify-user", "Notify User",
            "Send a notification message to the user",
            ["send-notification", "alert-user", "message-user"]),
        new("query-availability", "Query Availability",
            "Check if the user is currently available busy or away",
            ["check-availability", "is-available", "user-status", "availability-check"]),
        new("negotiate-meeting", "Negotiate Meeting",
            "Schedule or negotiate a meeting with the user including time purpose and duration",
            ["schedule-meeting", "book-meeting", "arrange-meeting", "meeting-request",
             "request-meeting", "set-up-meeting", "plan-meeting"])
    ];

    /// <summary>
    /// Attempts to match <paramref name="requestedSkillId"/> to a known built-in skill.
    /// Returns the canonical skill ID if a match is found, or <c>null</c> if no
    /// confident match exists (the request should fall to the Observe path).
    /// </summary>
    public static string? Match(string requestedSkillId)
    {
        if (string.IsNullOrWhiteSpace(requestedSkillId))
            return null;

        var normalized = requestedSkillId.Trim().ToLowerInvariant();

        // 1. Exact match on canonical ID
        foreach (var skill in BuiltInSkills)
        {
            if (skill.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                return skill.Id;
        }

        // 2. Exact match on a known alias
        foreach (var skill in BuiltInSkills)
        {
            foreach (var alias in skill.Aliases)
            {
                if (alias.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    return skill.Id;
            }
        }

        // 3. BM25 fuzzy match against skill ID + name + description + aliases
        var ranked = Bm25Ranker.RankWithScores(
            BuiltInSkills,
            skill => $"{skill.Id} {string.Join(' ', skill.Aliases)} {skill.Name} {skill.Description}",
            normalized);

        if (ranked.Count == 0)
            return null;

        var best = ranked[0];

        // Single candidate with any score — accept it
        if (ranked.Count == 1)
            return best.Item.Id;

        // Multiple candidates — only accept if the top result is clearly dominant
        if (ranked.Count >= 2 && ranked[1].Score / best.Score > MinScoreRatio)
        {
            // Ambiguous — the top two scores are too close
            return null;
        }

        return best.Item.Id;
    }

    private sealed record SkillDefinition(
        string Id,
        string Name,
        string Description,
        string[] Aliases);
}
