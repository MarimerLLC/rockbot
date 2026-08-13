namespace RockBot.Host;

/// <summary>
/// Outcome of a partial, exact-match edit applied inside a store — long-term memory,
/// working memory, a skill body, a scheduled-task directive, or a rule.
/// </summary>
/// <param name="IsSuccess">Whether the edit was applied and persisted.</param>
/// <param name="Error">
/// Human-readable failure description, suitable for returning to the model verbatim;
/// <c>null</c> on success.
/// </param>
/// <param name="ReplacementCount">Number of occurrences replaced. Zero unless successful.</param>
/// <param name="OldLength">Character length of the content before the edit.</param>
/// <param name="NewLength">Character length of the content after the edit.</param>
/// <remarks>
/// Deliberately not a second copy of the primitive's status enum: a store that refuses an
/// edit passes the primitive's own <c>Error</c> straight through, so there is exactly one
/// place in the codebase that decides how a refusal is worded.
/// </remarks>
public readonly record struct ContentEditResult(
    bool IsSuccess,
    string? Error,
    int ReplacementCount,
    int OldLength,
    int NewLength)
{
    /// <summary>A refusal carrying <paramref name="error"/> as its explanation.</summary>
    public static ContentEditResult Failed(string error) => new(false, error, 0, 0, 0);

    /// <summary>An applied edit.</summary>
    public static ContentEditResult Applied(int replacementCount, int oldLength, int newLength) =>
        new(true, null, replacementCount, oldLength, newLength);

    /// <summary>
    /// The refusal every interface's default implementation returns — stores that have
    /// not opted into partial edits (in-memory test doubles, null-object stores).
    /// </summary>
    public static ContentEditResult NotSupported { get; } =
        Failed("This store does not support partial edits.");
}
