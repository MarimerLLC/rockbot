namespace RockBot.Host;

/// <summary>
/// Decides whether a capitalized word sits where English grammar would have capitalized it
/// anyway, which is the only position where the merge-coverage common-word list should apply.
/// </summary>
/// <remarks>
/// <para>
/// Mid-sentence capitalization is evidence of a proper noun. Sentence-initial capitalization
/// is evidence of nothing. Treating the two alike is what forced the common-word list into an
/// unwinnable trade-off: a live corpus needed "Valid", "Direct", "Alternative" and "Through"
/// suppressed as sentence openers, while "Personal", "Class", "Benefit" and "Extended" had to
/// stay protected because they name real things mid-phrase ("OneDrive Personal", "Blazor
/// Online Class", "MVP Azure Extended Benefit"). Splitting by position dissolves the conflict
/// and makes the list safe to extend.
/// </para>
/// <para>
/// Errs toward "not sentence-initial", because that is the direction that keeps a word
/// required to survive a merge.
/// </para>
/// </remarks>
internal static class SentencePosition
{
    /// <summary>
    /// Abbreviations whose trailing period does not end a sentence. Without this, "St. Paul"
    /// and "Dr. May" read as sentence boundaries, putting the following word in the position
    /// where the common-word list applies — which is exactly where a storytelling agent's
    /// character named May or Rose would silently lose coverage protection.
    /// </summary>
    private static readonly HashSet<string> NonTerminalAbbreviations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "st", "dr", "mr", "mrs", "ms", "jr", "sr", "prof", "rev", "hon",
            "gen", "col", "sgt", "lt", "capt", "inc", "ltd", "co", "corp",
            "no", "vs", "etc", "eg", "ie", "fig", "approx", "dept", "est",
        };

    /// <summary>
    /// True when the token starting at <paramref name="index"/> opens a sentence, a line, or a
    /// list item.
    /// </summary>
    internal static bool IsSentenceInitial(string content, int index)
    {
        var i = index - 1;

        // Opening quotes and brackets never end a sentence, so step over them before looking
        // for a terminator: >He said. "Personal data …< still opens a sentence at "Personal".
        while (i >= 0 && content[i] is '"' or '\'' or '(' or '[' or '{' or '“' or '‘' or '«')
            i--;

        while (i >= 0 && char.IsWhiteSpace(content[i]))
        {
            // A line break starts a fresh clause regardless of how the previous line ended.
            // Memory content is frequently bulleted or newline-delimited rather than punctuated.
            if (content[i] is '\n' or '\r')
                return true;
            i--;
        }

        if (i < 0)
            return true;

        // A bullet marker counts only when nothing but whitespace precedes it on the line;
        // otherwise this is a hyphen inside running text.
        if (content[i] is '-' or '*' or '•' or '–' or '—')
            return IsAtLineStart(content, i);

        var prev = content[i];
        if (prev is not ('.' or '!' or '?' or ':' or ';'))
            return false;

        return prev != '.' || !EndsWithNonTerminalAbbreviation(content, i);
    }

    private static bool IsAtLineStart(string content, int markerIndex)
    {
        for (var i = markerIndex - 1; i >= 0; i--)
        {
            if (content[i] is '\n' or '\r')
                return true;
            if (!char.IsWhiteSpace(content[i]))
                return false;
        }

        return true;
    }

    private static bool EndsWithNonTerminalAbbreviation(string content, int periodIndex)
    {
        var end = periodIndex;
        var start = end;
        while (start > 0 && char.IsLetter(content[start - 1]))
            start--;

        return start != end
            && NonTerminalAbbreviations.Contains(content[start..end]);
    }
}
