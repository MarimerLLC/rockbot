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
    // The paragraph must be at the end of the text (after a blank line or at string end)
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
    [GeneratedRegex(
        @"(\r?\n){2,}" +                               // blank line separator
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
}
