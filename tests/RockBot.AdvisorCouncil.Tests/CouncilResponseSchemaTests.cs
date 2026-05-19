using System.Text.Json;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Tests;

[TestClass]
public class CouncilResponseSchemaTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [TestMethod]
    public void CouncilResponse_RoundTrips()
    {
        var original = new CouncilResponse(
            Question: "What is X?",
            Personas:
            [
                new PersonaView("skeptic", "skeptic view", ["pt1", "pt2"], []),
                new PersonaView("engineer", "engineer view", ["pt3"], ["http://example.com"])
            ],
            Tensions:
            [
                new Tension(["skeptic", "engineer"], "disagree about feasibility", "ship vs. delay")
            ],
            Synthesis: "## Synthesis\n\nThe council...",
            Confidence: "medium",
            Metadata: new CouncilMetadata(
                CritiqueRun: true,
                PreResearchRun: false,
                PersonaCount: 2,
                DurationMs: 12345,
                ModelCalls: 6,
                PersonaSetHash: "abc123",
                SelectorRationale: "selected based on contested feasibility"));

        var json = JsonSerializer.Serialize(original, Opts);
        var roundTripped = JsonSerializer.Deserialize<CouncilResponse>(json, Opts);

        Assert.IsNotNull(roundTripped);
        Assert.AreEqual(original.Question, roundTripped!.Question);
        Assert.AreEqual(original.Personas.Count, roundTripped.Personas.Count);
        Assert.AreEqual("skeptic", roundTripped.Personas[0].Id);
        Assert.AreEqual(original.Tensions.Count, roundTripped.Tensions.Count);
        Assert.AreEqual(original.Confidence, roundTripped.Confidence);
        Assert.AreEqual(original.Metadata.DurationMs, roundTripped.Metadata.DurationMs);
        Assert.AreEqual(original.Metadata.PersonaSetHash, roundTripped.Metadata.PersonaSetHash);
    }

    [TestMethod]
    public void SelectorOutput_JsonSchemaShape_IsSnakeCase()
    {
        var sel = new SelectorOutput(
            [new SelectedPersona("skeptic")],
            PreResearch: true,
            Critique: false,
            Rationale: "r");

        var json = JsonSerializer.Serialize(sel);

        StringAssert.Contains(json, "\"pre_research\":");
        StringAssert.Contains(json, "\"critique\":");
        StringAssert.Contains(json, "\"rationale\":");
        Assert.IsFalse(json.Contains("needs_research", StringComparison.Ordinal),
            "needs_research was removed; SelectedPersona should serialize with only id");
    }
}
