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
    /// When <paramref name="content"/> uses CRLF line endings and
    /// <paramref name="oldText"/> does not match as supplied, the match is retried with
    /// its bare LFs converted to CRLF. This lets a caller that emits Unix newlines edit
    /// a Windows-authored document without having to know the file's line-ending style.
    /// <paramref name="newText"/> is converted the same way so the file stays internally
    /// consistent.
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
        var effectiveNew = newText;
        var count = CountOccurrences(content, effectiveOld);

        // The file is CRLF but the caller supplied bare LFs (or vice versa). Retry once
        // with the caller's newlines converted to match the file, so line-ending style
        // is not something the caller has to discover by trial and error.
        if (count == 0 && content.Contains("\r\n", StringComparison.Ordinal))
        {
            var crlfOld = ToCrLf(oldText);
            if (!string.Equals(crlfOld, oldText, StringComparison.Ordinal))
            {
                var crlfCount = CountOccurrences(content, crlfOld);
                if (crlfCount > 0)
                {
                    effectiveOld = crlfOld;
                    effectiveNew = ToCrLf(newText);
                    count = crlfCount;
                }
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
    /// Converts bare LF line endings to CRLF, leaving existing CRLF pairs intact.
    /// </summary>
    private static string ToCrLf(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace("\n", "\r\n", StringComparison.Ordinal);
}
