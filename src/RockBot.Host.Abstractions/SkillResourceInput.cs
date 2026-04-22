namespace RockBot.Host;

/// <summary>
/// A sub-resource to be saved alongside a skill — as supplied in a <c>save_skill</c> call.
/// Carries both the manifest metadata and the file content.
/// </summary>
/// <param name="Filename">
/// The filename for this resource (e.g. <c>script.py</c>, <c>schema.json</c>).
/// Simple filename only — no path separators.
/// </param>
/// <param name="Type">The type of the resource.</param>
/// <param name="Description">A short description of what this resource does.</param>
/// <param name="Content">The full text content of the resource file.</param>
public sealed record SkillResourceInput(
    string Filename,
    SkillResourceType Type,
    string Description,
    string Content);
