namespace RockBot.Host;

/// <summary>
/// Describes a single sub-resource file belonging to a skill — as stored in the skill's manifest.
/// </summary>
/// <param name="Filename">
/// The filename of the resource (e.g. <c>script.py</c>, <c>schema.json</c>).
/// Simple filename only — no path separators.
/// </param>
/// <param name="Type">The type of the resource, used for LLM guidance and tooling.</param>
/// <param name="Description">A short description of what this resource does.</param>
public sealed record SkillResource(
    string Filename,
    SkillResourceType Type,
    string Description);
