using System.Text.Json.Serialization;

namespace RockBot.AdvisorCouncil.Schema;

/// <summary>
/// Output of the Select step. Decides which personas participate, whether to run
/// pre-research, and whether to run cross-critique. Surfaced verbatim in the council
/// response metadata for debuggability.
/// </summary>
internal sealed record SelectorOutput(
    [property: JsonPropertyName("personas")] IReadOnlyList<SelectedPersona> Personas,
    [property: JsonPropertyName("pre_research")] bool PreResearch,
    [property: JsonPropertyName("critique")] bool Critique,
    [property: JsonPropertyName("rationale")] string Rationale);

internal sealed record SelectedPersona(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("needs_research")] bool NeedsResearch);
