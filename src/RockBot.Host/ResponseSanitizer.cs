using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Post-processes LLM output to strip patterns that the model produces despite
/// explicit system prompt instructions to the contrary (trailing offer menus,
/// clickbait teasers, "would you like me to..." closings).
/// </summary>
public static partial class ResponseSanitizer
{
    // Matches a trailing paragraph that starts with an offer/teaser pattern.
    // The offer must be at the end of the text (after one or more newlines)
    // and begin with a recognisable offer phrase.
    //
    // Covers patterns like:
    //   "If you want, I can also..."
    //   "Would you like me to..."
    //   "I can also..."
    //   "Want me to..."
    //   "Should I..."
    //   "Let me know if you'd like..."
    //   "I could also..."
    //   "Next logical step is... I can do that now."
    //   Bullet lists that start with "I can also" / "If you want"
    //
    // Uses {1,} (not {2,}) so offers after a single newline are caught too —
    // LLMs don't always insert a blank line before the trailing teaser.
    [GeneratedRegex(
        @"(\r?\n){1,}" +                               // one or more newlines
        @"(?:" +
            @"(?:If you (?:want|'d like|would like),?\s*I\s)" +  // "If you want, I ..."
            @"|(?:Would you like me to\s)" +                      // "Would you like me to ..."
            @"|(?:Want me to\s)" +                                // "Want me to ..."
            @"|(?:Should I\s)" +                                  // "Should I ..."
            @"|(?:Let me know if\s)" +                            // "Let me know if ..."
            @"|(?:I (?:can|could) also\s)" +                      // "I can/could also ..."
            @"|(?:Next logical step\s)" +                         // "Next logical step ..."
            @"|(?:If you(?:'d)? like,?\s)" +                      // "If you like, ..."
        @")" +
        @"[\s\S]*$",                                    // consume everything to end
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TrailingOfferPattern();

    /// <summary>
    /// Strips trailing offer/teaser paragraphs from the LLM response.
    /// Returns the original text if no pattern matches, or the trimmed text
    /// with trailing whitespace cleaned up.
    /// </summary>
    public static string StripTrailingOffers(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = TrailingOfferPattern().Replace(text, string.Empty).TrimEnd();

        // Don't strip if it would remove most of the content — only strip
        // trailing fluff, not the substance.
        if (result.Length < text.Length * 0.3)
            return text;

        return result;
    }

    // Matches a trailing paragraph that narrates a memory write — the production
    // signature from issue #397, where the model answers the user properly and then
    // appends a sentence reporting what it stored:
    //
    //   "I've marked it as a winter trip goal tied to your joints and the dry air."
    //   "I've got Cathedral City in the travel picture now, ..."
    //   "Noted it in memory."
    //
    // Three parts must all be present, which is what keeps legitimate outcome reports
    // out of the pattern:
    //
    //   1. a first-person memory-write verb,
    //   2. a pronoun or proper-noun object. "I've saved the file to /tmp/x" reports a
    //      real outcome the user needs to hear; the lowercase determiner keeps it out,
    //      while "I've got Cathedral City in the travel picture" — the same narration
    //      with a named subject — stays in. The proper-noun branch is matched
    //      case-sensitively via (?-i:…) since the pattern as a whole ignores case.
    //   3. memory-storage vocabulary, with todo/calendar/reminder targets excluded
    //      because those describe genuine tool actions rather than memory narration.
    //
    // Anchored to a trailing paragraph the same way TrailingOfferPattern is; a reply
    // that is *entirely* narration is left to the AgentLoopRunner re-prompt guard.
    [GeneratedRegex(
        @"(\r?\n){1,}" +                                        // one or more newlines
        @"(?:Also,?\s|And\s|Noted[.,]?\s)?" +                   // optional lead-in
        @"I(?:'ve|\s+have|\s+am|'m)?\s*" +                      // "I", "I've", "I have", "I'm"
        @"(?:also\s+)?" +
        @"(?:marked|logged|noted|saved|stored|got|added|put|recorded|captured|filed|" +
            @"keeping|kept|holding|held)\s+" +                  // memory-write verb
        @"(?:it|that|this|them|(?-i:[A-Z])[\w'’-]*(?:\s+[\w'’-]+){0,3})\b" +  // object (see note 2)
        @"(?![^\n]*\b(?:todo|to-do|task list|calendar|reminder|shopping list|" +
            @"invite|email|draft|file|repo|branch|issue|pull request)\b)" +  // real-action exclusions
        @"[^\n]*\b(?:memor(?:y|ies)|ledger|board|wishlist|list|notes?|record|" +
            @"on file|in mind|for later|down|goal|picture|profile)\b" +      // memory vocabulary
        @"[^\n]*(?:\r?\n[^\n]+)*$",                             // consume the trailing paragraph
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TrailingMemoryNarrationPattern();

    /// <summary>
    /// Strips a trailing paragraph that narrates a memory write ("I've marked it as a
    /// winter trip goal…") while leaving the substantive reply intact. Returns the
    /// original text when nothing matches, or when stripping would remove most of the
    /// content — a reply that is *only* narration is not silently emptied; the
    /// AgentLoopRunner memory-summary guard re-prompts for that case instead.
    /// </summary>
    /// <remarks>
    /// Callers must only apply this when a memory write actually happened this turn
    /// (see <c>AgentLoopRunner.SavedMemoryThisTurn</c>). Without that gate a reply
    /// legitimately reporting some other stored outcome could be trimmed. See issue #397.
    /// </remarks>
    public static string StripTrailingMemoryNarration(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var result = TrailingMemoryNarrationPattern().Replace(text, string.Empty).TrimEnd();

        // Same substance guard as StripTrailingOffers: only trailing narration goes,
        // never the answer itself.
        if (result.Length < text.Length * 0.3)
            return text;

        return result;
    }
}
