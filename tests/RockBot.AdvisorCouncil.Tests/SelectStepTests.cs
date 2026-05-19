using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;

namespace RockBot.AdvisorCouncil.Tests;

[TestClass]
public class SelectStepTests
{
    private static PersonaRegistry MakeRegistry(params (string id, bool defaultResearch)[] personas)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "selector-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        foreach (var (id, dr) in personas)
        {
            File.WriteAllText(Path.Combine(tmp, $"{id}.md"),
                $"---\nid: {id}\ndefault_research: {dr.ToString().ToLower()}\n---\n{id} body.");
        }
        var opts = Options.Create(new CouncilOptions { PersonasPath = tmp });
        return new PersonaRegistry(opts, NullLogger<PersonaRegistry>.Instance);
    }

    [TestMethod]
    public async Task RunAsync_ValidJson_ReturnsParsedSelection()
    {
        var registry = MakeRegistry(("skeptic", false), ("engineer", true), ("economist", true));
        var chat = new FakeChatClient().EnqueueResponse(
            """
            { "personas": [{"id":"skeptic"},{"id":"engineer"}],
              "pre_research": false, "critique": true, "rationale": "Design question with engineering tradeoffs." }
            """);
        var step = new SelectStep(chat, registry, NullLogger<SelectStep>.Instance);

        var result = await step.RunAsync("Should we adopt MAF?", CancellationToken.None);

        Assert.AreEqual(2, result.Personas.Count);
        Assert.IsTrue(result.Critique);
        Assert.IsFalse(result.PreResearch);
        Assert.AreEqual("skeptic", result.Personas[0].Id);
        Assert.AreEqual("engineer", result.Personas[1].Id);
    }

    [TestMethod]
    public async Task RunAsync_InvalidJson_RetriesThenSucceeds()
    {
        var registry = MakeRegistry(("skeptic", false), ("engineer", false));
        var chat = new FakeChatClient()
            .EnqueueResponse("not json")
            .EnqueueResponse(
                """{ "personas": [{"id":"skeptic"}], "pre_research": false, "critique": false, "rationale": "x" }""");
        var step = new SelectStep(chat, registry, NullLogger<SelectStep>.Instance);

        var result = await step.RunAsync("Trivial", CancellationToken.None);

        Assert.AreEqual(1, result.Personas.Count);
        Assert.AreEqual(2, chat.Calls.Count, "Should have retried after invalid JSON");
    }

    [TestMethod]
    public async Task RunAsync_TwoFailures_FallsBackToDefault()
    {
        var registry = MakeRegistry(("skeptic", false), ("engineer", false), ("long_term", false), ("ethicist", false));
        var chat = new FakeChatClient()
            .EnqueueResponse("not json")
            .EnqueueResponse("also not json");
        var step = new SelectStep(chat, registry, NullLogger<SelectStep>.Instance);

        var result = await step.RunAsync("Anything", CancellationToken.None);

        Assert.IsFalse(result.PreResearch);
        Assert.IsFalse(result.Critique);
        Assert.IsTrue(result.Personas.Count >= 2);
        CollectionAssert.Contains(result.Personas.Select(p => p.Id).ToList(), "skeptic");
        CollectionAssert.Contains(result.Personas.Select(p => p.Id).ToList(), "engineer");
    }

    [TestMethod]
    public async Task RunAsync_UnknownPersonaId_FilteredOut()
    {
        var registry = MakeRegistry(("skeptic", false), ("engineer", false));
        var chat = new FakeChatClient().EnqueueResponse(
            """{ "personas": [{"id":"unknown"},{"id":"skeptic"}], "pre_research": false, "critique": false, "rationale": "x" }""");
        var step = new SelectStep(chat, registry, NullLogger<SelectStep>.Instance);

        var result = await step.RunAsync("q", CancellationToken.None);

        Assert.AreEqual(1, result.Personas.Count);
        Assert.AreEqual("skeptic", result.Personas[0].Id);
    }
}
