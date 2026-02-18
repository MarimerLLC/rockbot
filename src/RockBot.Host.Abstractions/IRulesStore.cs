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
}
