using System.Text.Json.Serialization;

namespace RockBot.AdvisorCouncil.Schema;

/// <summary>
/// Output of the Select step. Decides which personas participate, whether to run
/// pre-research, and whether to run cross-critique. Surfaced verbatim in the council
/// response metadata for debuggability.
/// </summary>
/// <param name="Missing">
/// Choice fields (<see cref="CouncilChoices.AskableFields"/>) that nobody supplied and that would
/// change the plan. The council may ask about these once, before it deliberates.
/// </param>
/// <param name="TooThin">
/// True when the question can't be deliberated on at all without more background; the council
/// returns early and asks the caller for <c>context</c> instead of running.
/// </param>
/// <param name="Needs">When <paramref name="TooThin"/>: what the caller's <c>context</c> should cover.</param>
internal sealed record SelectorOutput(
    [property: JsonPropertyName("personas")] IReadOnlyList<SelectedPersona> Personas,
    [property: JsonPropertyName("pre_research")] bool PreResearch,
    [property: JsonPropertyName("critique")] bool Critique,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("missing")] IReadOnlyList<string>? Missing = null,
    [property: JsonPropertyName("too_thin")] bool TooThin = false,
    [property: JsonPropertyName("needs")] IReadOnlyList<string>? Needs = null);

internal sealed record SelectedPersona(
    [property: JsonPropertyName("id")] string Id);
