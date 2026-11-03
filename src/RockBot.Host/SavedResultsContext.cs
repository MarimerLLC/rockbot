namespace RockBot.Host;

/// <summary>
/// Picks saved results that match the user's message, for a short context note (#418). Live test:
/// asked for "the BBR vs CUBIC comparison you did today", the agent never called
/// <c>list_saved_responses</c>. It rebuilt the answer from leftover working memory instead, because
/// that was in its context and the saved copy wasn't. Listing a matching saved result next to
/// the request makes the saved copy the obvious source.
/// </summary>
public static class SavedResultsContext
{
    /// <summary>Most matches listed.</summary>
    public const int MaxMatches = 3;

    /// <summary>Content characters searched per entry; the topic is near the top.</summary>
    internal const int SearchedContentChars = 4_000;

    /// <summary>
    /// Conversational filler that says nothing about the topic. Without this, any message would
    /// match any entry on words like "you", "the" or "earlier".
    /// </summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "but", "not", "you", "your", "yours", "our", "are", "was", "were", "has",
        "have", "had", "did", "does", "doing", "done", "can", "could", "would", "should", "will", "shall",
        "may", "might", "must", "that", "this", "these", "those", "with", "from", "into", "onto", "about",
        "what", "when", "where", "which", "who", "whom", "why", "how", "just", "also", "too", "very",
        "some", "any", "all", "each", "every", "more", "most", "much", "many", "such", "than", "then",
        "there", "here", "its", "it's", "they", "them", "their", "she", "her", "him", "his", "get", "got",
        "give", "gave", "make", "made", "take", "want", "need", "like", "know", "think", "please", "thanks",
        "thank", "again", "back", "earlier", "before", "today", "yesterday", "tonight", "morning",
        "afternoon", "evening", "week", "now", "lost", "gone", "missing", "screen", "find", "show", "tell",
        "let", "yes", "okay", "sure", "one", "two", "out", "off", "over", "under", "did", "dont", "don",
        "didn", "isn", "wasn", "can't", "saved", "save", "result", "results", "report", "answer",
        "response", "research", "researched", "thing", "stuff", "something", "anything", "everything",
    };

    /// <summary>
    /// The saved entries that share enough topic words with <paramref name="userMessage"/>, best
    /// match first: at least 2 distinct words, or 3 when the message has 6 or more topic words.
    /// Ties go to the newer entry.
    /// </summary>
    public static IReadOnlyList<SavedResponse> SelectMatches(
        IReadOnlyList<SavedResponse> saved, string? userMessage, int max = MaxMatches)
    {
        if (saved.Count == 0 || string.IsNullOrWhiteSpace(userMessage))
            return [];

        var terms = Bm25Ranker.Tokenize(userMessage)
            .Where(t => !Filler.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (terms.Count == 0)
            return [];

        // One shared word ("acme") is too weak to claim the user wants that saved result.
        var required = terms.Count >= 6 ? 3 : 2;

        return saved
            .Select(r =>
            {
                var text = r.Label + " " +
                           (r.Content.Length <= SearchedContentChars ? r.Content : r.Content[..SearchedContentChars]);
                var tokens = Bm25Ranker.Tokenize(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (Entry: r, Shared: terms.Count(tokens.Contains));
            })
            .Where(x => x.Shared >= required)
            .OrderByDescending(x => x.Shared)
            .ThenByDescending(x => x.Entry.SavedAt)
            .Take(max)
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>The context note listing <paramref name="matches"/>, or null when there are none.</summary>
    public static string? Render(IReadOnlyList<SavedResponse> matches)
    {
        if (matches.Count == 0)
            return null;

        var lines = matches.Select(r =>
            $"- id={r.Id} | saved {r.SavedAt:yyyy-MM-dd HH:mm} UTC | {r.Source ?? SavedResponseSources.User} | " +
            $"{r.Label} ({r.Content.Length:N0} chars)");

        return "Saved results that may match this request (kept after conversation history expires):\n" +
               string.Join("\n", lines) + "\n" +
               "If the user is asking for earlier work, read the match with get_saved_response(id) and give it " +
               "to them, saying when it was produced, instead of redoing the work or rebuilding it from working " +
               "memory. Offer to refresh it if it may be out of date. Ignore these if the request is about " +
               "something else.";
    }
}
