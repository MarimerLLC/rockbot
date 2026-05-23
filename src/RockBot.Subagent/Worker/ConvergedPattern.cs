using System.Text.Json.Serialization;

namespace RockBot.Subagent.Worker;

/// <summary>
/// A tool-call pattern the worker observed converging on success — typically
/// after one or more failed attempts. Surfaced in <see cref="WorkerResult"/>
/// so the spawning agent (which has the skill/identity context the lean
/// worker lacks) can decide whether to call <c>promote_skill_asset</c>.
/// </summary>
public sealed record ConvergedPattern
{
    /// <summary>Human-readable summary of what the pattern accomplishes.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// Type of resource the body represents — e.g. <c>"wisp"</c>, <c>"script"</c>,
    /// <c>"schema"</c>. Matches <c>SkillResourceType</c> naming so the spawning
    /// agent can promote it directly without re-classifying.
    /// </summary>
    [JsonPropertyName("body_type")]
    public required string BodyType { get; init; }

    /// <summary>The actual artifact (definition JSON, script source, schema).</summary>
    [JsonPropertyName("body")]
    public required string Body { get; init; }

    /// <summary>
    /// Optional hint describing how a future caller would know the pattern still
    /// works — feeds into the manifest <c>VerifyHint</c> when the spawning agent
    /// promotes the pattern.
    /// </summary>
    [JsonPropertyName("verify_hint")]
    public string? VerifyHint { get; init; }
}
