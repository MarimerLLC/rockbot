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
/// <param name="Provisional">
/// True when the resource is captured by the in-session promotion path or by a self-repair
/// attach — i.e. before it has been validated by repeated successful use. False for entries
/// promoted by the dream-cycle success pass (those land already validated).
/// </param>
/// <param name="VerifyHint">
/// Optional advisory free text describing how a future session would know the asset still works.
/// Persisted on the manifest entry; retained even after the entry is validated.
/// </param>
public sealed record SkillResourceInput(
    string Filename,
    SkillResourceType Type,
    string Description,
    string Content,
    bool Provisional = false,
    string? VerifyHint = null);
