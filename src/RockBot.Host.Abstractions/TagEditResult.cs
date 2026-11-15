namespace RockBot.Host;

/// <summary>
/// Outcome of an in-place tag edit on a long-term memory entry
/// (<see cref="ILongTermMemory.EditTagsAsync"/>).
/// </summary>
/// <param name="IsSuccess">Whether the request was valid and the entry exists. A no-op is a success.</param>
/// <param name="Error">
/// Human-readable failure description, suitable for returning to the model verbatim;
/// <c>null</c> on success.
/// </param>
/// <param name="Before">The entry's tags before the edit.</param>
/// <param name="After">The entry's tags after the edit (equal to <paramref name="Before"/> on a no-op).</param>
/// <param name="Added">Tags that were not on the entry and now are.</param>
/// <param name="Removed">Tags that were on the entry and no longer are, as they were stored.</param>
/// <param name="AlreadyPresent">Requested additions the entry already carried.</param>
/// <param name="NotPresent">Requested removals the entry did not carry.</param>
public readonly record struct TagEditResult(
    bool IsSuccess,
    string? Error,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> AlreadyPresent,
    IReadOnlyList<string> NotPresent)
{
    /// <summary>True when the request succeeded but left the tags exactly as they were.</summary>
    public bool IsNoOp => IsSuccess && Added.Count == 0 && Removed.Count == 0;

    /// <summary>A refusal carrying <paramref name="error"/> as its explanation.</summary>
    public static TagEditResult Failed(string error) => new(false, error, [], [], [], [], [], []);

    /// <summary>An applied (or no-op) tag edit.</summary>
    public static TagEditResult Applied(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        IReadOnlyList<string> alreadyPresent,
        IReadOnlyList<string> notPresent) =>
        new(true, null, before, after, added, removed, alreadyPresent, notPresent);

    /// <summary>
    /// The refusal the interface's default implementation returns — stores that have not
    /// opted into in-place tag edits (in-memory test doubles, null-object stores).
    /// </summary>
    public static TagEditResult NotSupported { get; } =
        Failed("This store does not support editing tags in place.");
}
