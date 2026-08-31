namespace RockBot.Host;

/// <summary>
/// The composed agent profile built from soul, directives, and optional style documents.
/// </summary>
/// <param name="Soul">Who the agent IS — stable personality document.</param>
/// <param name="Directives">HOW the agent operates — deployment-specific instructions.</param>
/// <param name="Style">Optional voice/tone document for user-facing agents.</param>
/// <param name="MemoryRules">Optional shared memory rules document included in every system prompt.</param>
/// <param name="SubagentDirectives">Optional subagent-specific directives (replaces <paramref name="Directives"/> for subagent prompts).</param>
/// <param name="CommonDirectives">Optional shared directives included in both primary and subagent prompts.</param>
/// <param name="WorkerDirectives">Optional worker-specific directives (slim ruleset for the lean worker rung).</param>
/// <param name="SafetyRules">Optional safety rules snippet included by every rung (primary, subagent, worker).</param>
public sealed record AgentProfile(
    AgentProfileDocument Soul,
    AgentProfileDocument Directives,
    AgentProfileDocument? Style = null,
    AgentProfileDocument? MemoryRules = null,
    AgentProfileDocument? SubagentDirectives = null,
    AgentProfileDocument? CommonDirectives = null,
    AgentProfileDocument? WorkerDirectives = null,
    AgentProfileDocument? SafetyRules = null)
{
    /// <summary>
    /// All loaded documents in composition order for the primary agent:
    /// soul, safety-rules (if present), common-directives (if present), directives, memory-rules (if present), style (if present).
    /// </summary>
    public IReadOnlyList<AgentProfileDocument> Documents { get; } =
        new[] { Soul, SafetyRules, CommonDirectives, Directives, MemoryRules, Style }
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
