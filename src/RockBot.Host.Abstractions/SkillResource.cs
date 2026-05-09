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
/// <param name="Provisional">
/// True for resources captured by the in-session promotion path (subagent <c>promote_skill_asset</c>
/// or self-repair attach) before they have been validated by repeated successful use.
/// The skill index renders provisional types with a trailing <c>*</c> (e.g. <c>[Wisp*]</c>).
/// Flipped to false by the dream-cycle validation pass once the resource has succeeded
/// repeatedly in distinct sessions.
/// </param>
/// <param name="CreatedAt">
/// Wall-clock time the manifest entry was first written. Used by the validation pass to
/// scope the success/failure window and by the staleness sweep to evict unused entries.
/// </param>
/// <param name="VerifyHint">
/// Optional advisory free text describing how a future session would know the asset still works
/// (e.g. "calls get_calendar_events for both accounts and returns per-account event arrays").
/// Retained even after the entry is validated — it documents the asset's intended exercise.
/// </param>
/// <param name="DefinitionHash">
/// SHA-256-hex16 of the resource <em>content</em> at the time of save, matching the hash
/// scheme used by the wisp execution log. The validation pass cross-references this against
/// recent wisp records to count successes and failures of the captured pattern. Null for
/// resources saved before this field existed and for non-hashed resource types.
/// </param>
public sealed record SkillResource(
    string Filename,
    SkillResourceType Type,
    string Description,
    bool Provisional = false,
    DateTimeOffset? CreatedAt = null,
    string? VerifyHint = null,
    string? DefinitionHash = null);
