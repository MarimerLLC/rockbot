namespace RockBot.Host;

/// <summary>
/// The composed agent profile built from soul, directives, and optional style documents.
/// </summary>
/// <param name="Soul">Who the agent IS — stable personality document.</param>
/// <param name="Directives">HOW the agent operates — deployment-specific instructions.</param>
/// <param name="Style">Optional voice/tone document for user-facing agents.</param>
/// <param name="MemoryRules">Optional shared memory rules document included in every system prompt.</param>
public sealed record AgentProfile(
    AgentProfileDocument Soul,
    AgentProfileDocument Directives,
    AgentProfileDocument? Style = null,
    AgentProfileDocument? MemoryRules = null)
{
    /// <summary>
    /// All loaded documents in composition order: soul, directives, memory-rules (if present), style (if present).
    /// </summary>
    public IReadOnlyList<AgentProfileDocument> Documents { get; } =
        new[] { Soul, Directives, MemoryRules, Style }
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

    /// <summary>
    /// Finds a section by name across all documents (first match wins).
    /// </summary>
    /// <param name="name">Case-insensitive section heading to search for.</param>
    /// <returns>The matching section, or null if not found.</returns>
    public AgentProfileSection? FindSection(string name)
    {
        foreach (var doc in Documents)
        {
            foreach (var section in doc.Sections)
            {
                if (section.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return section;
            }
        }

        return null;
    }
}
