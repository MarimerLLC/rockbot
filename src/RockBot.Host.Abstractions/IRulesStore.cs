namespace RockBot.Host;

/// <summary>
/// Persistent store for hard behavioral rules that are always enforced,
/// regardless of conversation context. Rules are treated at the same
/// level as the agent's directives and injected into every system prompt.
/// </summary>
public interface IRulesStore
{
    /// <summary>Current in-memory list of active rules.</summary>
    IReadOnlyList<string> Rules { get; }

    /// <summary>Returns all active rules.</summary>
    Task<IReadOnlyList<string>> ListAsync();

    /// <summary>Adds a rule. No-ops if an identical rule already exists.</summary>
    Task AddAsync(string rule);

    /// <summary>Removes a rule by exact text (case-insensitive). No-ops if not found.</summary>
    Task RemoveAsync(string rule);

    /// <summary>
    /// Replaces an exact piece of text inside the active rules, leaving every other rule —
    /// and any surrounding document structure — untouched.
    /// </summary>
    /// <param name="oldText">Exact text to find within a rule. Must be non-empty.</param>
    /// <param name="newText">Replacement text. May be empty to delete the match.</param>
    /// <param name="replaceAll">
    /// When <c>true</c>, replaces every occurrence across every rule. When <c>false</c>, more
    /// than one occurrence anywhere in the rule set is refused rather than guessed at.
    /// </param>
    /// <remarks>
    /// The alternative — <see cref="RemoveAsync"/> then <see cref="AddAsync"/> — moves the
    /// rule to the end of the list and requires restating it in full to change one word of it.
    /// </remarks>
    Task<ContentEditResult> EditAsync(string oldText, string newText, bool replaceAll = false)
        => Task.FromResult(ContentEditResult.NotSupported);
}
