using System.Text.Json.Serialization;

namespace RockBot.AdvisorCouncil.Schema;

/// <summary>
/// Full structured output of a council run. The <c>advise</c> tool returns it as the result's
/// <c>structuredContent</c>; the synthesis prose is also the result's text content, for callers
/// that only read prose.
/// </summary>
/// <param name="Assumptions">
/// What the council assumed for anything it was not told (asked and declined, or never asked).
/// The synthesis is only as good as these, so the caller should see them.
/// </param>
internal sealed record CouncilResponse(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("personas")] IReadOnlyList<PersonaView> Personas,
    [property: JsonPropertyName("tensions")] IReadOnlyList<Tension> Tensions,
    [property: JsonPropertyName("synthesis")] string Synthesis,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("metadata")] CouncilMetadata Metadata,
    [property: JsonPropertyName("assumptions")] IReadOnlyList<string>? Assumptions = null);

internal sealed record PersonaView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("view")] string View,
    [property: JsonPropertyName("key_points")] IReadOnlyList<string> KeyPoints,
    [property: JsonPropertyName("sources")] IReadOnlyList<string> Sources,
    [property: JsonPropertyName("status")] string Status = PersonaStatus.Ok);

/// <summary>Standardized values for <see cref="PersonaView.Status"/>.</summary>
internal static class PersonaStatus
{
    /// <summary>The persona produced its view within the per-persona budget.</summary>
    public const string Ok = "ok";

    /// <summary>The persona was cancelled by the per-persona soft timeout.</summary>
    public const string TimedOut = "timed_out";

    /// <summary>The persona's chat-client call threw a non-cancellation exception.</summary>
    public const string Failed = "failed";
}

internal sealed record Tension(
    [property: JsonPropertyName("between")] IReadOnlyList<string> Between,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("stakes")] string Stakes);

internal sealed record CouncilMetadata(
    [property: JsonPropertyName("critique_run")] bool CritiqueRun,
    [property: JsonPropertyName("pre_research_run")] bool PreResearchRun,
    [property: JsonPropertyName("persona_count")] int PersonaCount,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("model_calls")] int ModelCalls,
    [property: JsonPropertyName("persona_set_hash")] string PersonaSetHash,
    [property: JsonPropertyName("selector_rationale")] string SelectorRationale,
    [property: JsonPropertyName("personas_contributed")] int PersonasContributed = 0,
    [property: JsonPropertyName("personas_missing")] IReadOnlyList<string>? PersonasMissing = null,
    [property: JsonPropertyName("clarification")] string Clarification = ClarificationStatus.NotAsked,
    [property: JsonPropertyName("critique_skipped")] bool CritiqueSkipped = false);
