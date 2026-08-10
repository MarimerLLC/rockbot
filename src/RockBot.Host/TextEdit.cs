namespace RockBot.Host;

/// <summary>
/// Outcome classification for an <see cref="TextEdit.Apply"/> call.
/// </summary>
public enum TextEditStatus
{
    /// <summary>The edit was applied.</summary>
    Success,

    /// <summary><c>oldText</c> does not occur in the content.</summary>
    NotFound,

    /// <summary>
    /// <c>oldText</c> occurs more than once and <c>replaceAll</c> was not set.
    /// The caller must supply more surrounding context to disambiguate.
    /// </summary>
    Ambiguous,

    /// <summary><c>oldText</c> was empty — an empty match has no well-defined location.</summary>
    EmptyOldText,

    /// <summary><c>oldText</c> and <c>newText</c> are identical, so the edit is a no-op.</summary>
    NoChange,
}

/// <summary>
/// Result of an exact-match text edit.
/// </summary>
/// <param name="Status">Outcome classification.</param>
/// <param name="Content">
/// The edited content when <see cref="Status"/> is <see cref="TextEditStatus.Success"/>;
/// <c>null</c> otherwise.
/// </param>
/// <param name="ReplacementCount">Number of occurrences replaced. Zero unless successful.</param>
/// <param name="Error">Human-readable failure description; <c>null</c> on success.</param>
public readonly record struct TextEditResult(
    TextEditStatus Status,
    string? Content,
    int ReplacementCount,
    string? Error)
{
    /// <summary>Whether the edit succeeded.</summary>
    public bool IsSuccess => Status == TextEditStatus.Success;
}

/// <summary>
/// Exact-match text replacement — the shared primitive behind surgical edits to
/// files, memory entries, and skill bodies.
/// </summary>
/// <remarks>
/// <para>
/// Every RockBot write surface historically replaced its entire payload: a one-word
/// correction to a document meant re-emitting the whole document, and anything the
/// model failed to reproduce was silently lost. This primitive exists so a caller can
/// state the change instead of restating the content.
/// </para>
/// <para>
/// Matching is ordinal and exact. Ambiguity is an error rather than a guess: when
/// <c>oldText</c> occurs more than once the caller must either widen the match with
/// surrounding context or opt in to <c>replaceAll</c>. That refusal is the point —
/// a tool that silently edits the first of several matches is worse than one that
/// declines, because the caller cannot tell which one it hit.
/// </para>
/// </remarks>
public static class TextEdit
{
    /// <summary>
    /// Replaces <paramref name="oldText"/> with <paramref name="newText"/> in
    /// <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The content to edit.</param>
    /// <param name="oldText">Exact text to find. Must be non-empty.</param>
    /// <param name="newText">Replacement text. May be empty to delete.</param>
    /// <param name="replaceAll">
    /// When <c>true</c>, replaces every occurrence. When <c>false</c> (default), more
    /// than one occurrence is an error.
    /// </param>
    /// <returns>A <see cref="TextEditResult"/> describing the outcome.</returns>
    /// <remarks>
    /// <para>
    /// When <paramref name="oldText"/> does not match as supplied, the match is retried
    /// with its line endings converted — bare LFs to CRLF, then CRLFs to bare LF. This
    /// lets a caller edit a document without having to know its line-ending style, in
    /// either direction.
    /// </para>
    /// <para>
    /// <paramref name="newText"/> is converted to the line-ending style
    /// <paramref name="content"/> already uses — on the exact-match path as well as the
    /// retry path — so an edit cannot leave a single-style document with mixed endings.
    /// Content that is already mixed is left alone, having no style to preserve.
    /// </para>
    /// </remarks>
    public static TextEditResult Apply(
        string content,
        string oldText,
        string newText,
        bool replaceAll = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        if (oldText.Length == 0)
        {
            return new TextEditResult(
                TextEditStatus.EmptyOldText,
                null,
                0,
                "oldText must not be empty — an empty match has no well-defined location. " +
                "To append content, include the trailing text you want to insert before.");
        }

        if (string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return new TextEditResult(
                TextEditStatus.NoChange,
                null,
                0,
                "oldText and newText are identical — the edit would change nothing.");
        }

        var effectiveOld = oldText;
        var effectiveNew = MatchNewlineStyle(newText, content);
        var count = CountOccurrences(content, effectiveOld);

        // The caller's line endings do not match the file's. Retry with each conversion
        // in turn — LF-supplied text against a CRLF file, and CRLF-supplied text against
        // an LF file — so line-ending style is not something the caller has to discover
        // by trial and error.
        if (count == 0)
        {
            (string Old, string New)[] candidates =
            [
                (ToCrLf(oldText), ToCrLf(newText)),
                (ToLf(oldText), ToLf(newText)),
            ];

            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Old, oldText, StringComparison.Ordinal))
                    continue;

                var candidateCount = CountOccurrences(content, candidate.Old);
                if (candidateCount == 0)
                    continue;

                effectiveOld = candidate.Old;
                effectiveNew = candidate.New;
                count = candidateCount;
                break;
            }
        }

        if (count == 0)
        {
            return new TextEditResult(
                TextEditStatus.NotFound,
                null,
                0,
                "oldText was not found. It must match the content exactly, including " +
                "whitespace and indentation. Read the current content and copy the text verbatim.");
        }

        // Newline normalization can collapse a difference that the raw arguments had —
        // "a\r\nb" replaced by "a\nb" in a CRLF file asks for no change at all.
        if (string.Equals(effectiveOld, effectiveNew, StringComparison.Ordinal))
        {
            return new TextEditResult(
                TextEditStatus.NoChange,
                null,
                0,
                "oldText and newText differ only in line endings, which are normalized to " +
                "the style the content already uses — the edit would change nothing.");
        }

        if (count > 1 && !replaceAll)
        {
            return new TextEditResult(
                TextEditStatus.Ambiguous,
                null,
                0,
                $"oldText occurs {count} times — the edit target is ambiguous. Either include " +
                "more surrounding text so the match is unique, or set replaceAll to change every occurrence.");
        }

        var edited = replaceAll
            ? content.Replace(effectiveOld, effectiveNew, StringComparison.Ordinal)
            : ReplaceFirst(content, effectiveOld, effectiveNew);

        return new TextEditResult(TextEditStatus.Success, edited, replaceAll ? count : 1, null);
    }

    /// <summary>
    /// Counts non-overlapping ordinal occurrences of <paramref name="needle"/>.
    /// </summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirst(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return index < 0
            ? content
            : string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
    }

    /// <summary>
    /// Converts <paramref name="value"/> to the line-ending style
    /// <paramref name="content"/> uses, or returns it unchanged when the content has no
    /// single style to preserve.
    /// </summary>
    private static string MatchNewlineStyle(string value, string content)
    {
        if (!value.Contains('\n', StringComparison.Ordinal))
            return value;

        var crLf = CountOccurrences(content, "\r\n");
        var bareLf = CountOccurrences(content, "\n") - crLf;

        if (crLf > 0 && bareLf == 0)
            return ToCrLf(value);

        if (bareLf > 0 && crLf == 0)
            return ToLf(value);

        // Mixed endings, or none at all — no style to conform to.
        return value;
    }

    /// <summary>
    /// Converts bare LF line endings to CRLF, leaving existing CRLF pairs intact.
    /// </summary>
    private static string ToCrLf(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>
    /// Converts CRLF line endings to bare LF.
    /// </summary>
    private static string ToLf(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
