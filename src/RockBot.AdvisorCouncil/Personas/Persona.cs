namespace RockBot.AdvisorCouncil.Personas;

/// <summary>
/// A persona is a framing the council uses to examine a question. Loaded from a markdown
/// file with YAML frontmatter on the personas PVC path.
/// </summary>
internal sealed record Persona(
    string Id,
    string Name,
    string Description,
    string SystemPrompt,
    bool DefaultResearch);
