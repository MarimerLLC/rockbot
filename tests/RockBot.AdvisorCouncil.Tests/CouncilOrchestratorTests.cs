using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;

namespace RockBot.AdvisorCouncil.Tests;

[TestClass]
public class CouncilOrchestratorTests
{
    private static PersonaRegistry MakeRegistry()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "orch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var personas = new[]
        {
            ("skeptic", "Skeptic body"),
            ("engineer", "Engineer body"),
            ("long_term", "Long-term body")
        };
        foreach (var (id, body) in personas)
            File.WriteAllText(Path.Combine(tmp, $"{id}.md"), $"---\nid: {id}\n---\n{body}");
        return new PersonaRegistry(
            Options.Create(new CouncilOptions { PersonasPath = tmp }),
            NullLogger<PersonaRegistry>.Instance);
    }

    [TestMethod]
    public async Task RunAsync_BasicPipeline_ReturnsExpectedShape()
    {
        var registry = MakeRegistry();
        // Matchers are checked in insertion order; put stage-distinguishing prompts
        // before persona-body prompts so critique calls (which carry both) don't
        // collide with the plain persona call match.
        var chat = new FakeChatClient()
            .WhenUserContains("selector for an advisor council",
                """{ "personas": [{"id":"skeptic"},{"id":"engineer"}], "pre_research": false, "critique": false, "rationale": "Engineering decision." }""")
            .WhenUserContains("synthesis step of an advisor council",
                """{ "synthesis": "## Synthesis\n\nProceed with caution.", "confidence": "medium", "tensions": [] }""")
            .WhenUserContains("Critique addendum",
                """{ "revised_view": "(revised)", "key_points": [], "tensions": [] }""")
            .WhenUserContains("Skeptic body", "Skeptic warns this is too early.")
            .WhenUserContains("Engineer body", "Engineering says feasibility is OK with caveats.");

        var orchestrator = BuildOrchestrator(registry, chat);

        var response = await orchestrator.RunAsync("Should we adopt MAF for orchestration?", "task-1", CancellationToken.None);

        Assert.AreEqual(2, response.Personas.Count);
        Assert.AreEqual("medium", response.Confidence);
        StringAssert.Contains(response.Synthesis, "Proceed with caution");
        Assert.IsFalse(response.Metadata.CritiqueRun);
        Assert.IsFalse(response.Metadata.PreResearchRun);
        Assert.AreEqual(2, response.Metadata.PersonaCount);
        Assert.IsFalse(string.IsNullOrEmpty(response.Metadata.PersonaSetHash));
        Assert.IsTrue(response.Metadata.ModelCalls >= 4); // select + 2 personas + synth
    }

    [TestMethod]
    public async Task RunAsync_CritiqueEnabled_RunsCritiqueAndPopulatesTensions()
    {
        var registry = MakeRegistry();
        var chat = new FakeChatClient()
            .WhenUserContains("selector for an advisor council",
                """{ "personas": [{"id":"skeptic"},{"id":"engineer"}], "pre_research": false, "critique": true, "rationale": "Contested." }""")
            .WhenUserContains("synthesis step of an advisor council",
                """{ "synthesis": "Integrated view.", "confidence": "low", "tensions": [{"between":["skeptic","engineer"],"description":"timing","stakes":"ship vs delay"}] }""")
            .WhenUserContains("Critique addendum",
                """{ "revised_view": "Revised view (post-critique)", "key_points": ["kp1"], "tensions": [{"with":"engineer","description":"disagree on timing","stakes":"ship vs delay"}] }""")
            .WhenUserContains("Skeptic body", "Initial skeptic view.")
            .WhenUserContains("Engineer body", "Initial engineer view.");

        var orchestrator = BuildOrchestrator(registry, chat);
        var response = await orchestrator.RunAsync("Should we adopt MAF?", "task-2", CancellationToken.None);

        Assert.IsTrue(response.Metadata.CritiqueRun);
        Assert.IsTrue(response.Tensions.Count >= 1);
        Assert.AreEqual("low", response.Confidence);
        Assert.IsTrue(response.Personas.Any(v => v.View.Contains("Revised")));
    }

    [TestMethod]
    public async Task RunAsync_SynthesisInvalid_FallsBackButStillReturns()
    {
        var registry = MakeRegistry();
        // Synthesis always returns invalid; orchestrator falls back to a stub synthesis.
        var chat = new FakeChatClient()
            .WhenUserContains("selector for an advisor council",
                """{ "personas": [{"id":"skeptic"}], "pre_research": false, "critique": false, "rationale": "Simple." }""")
            .WhenUserContains("synthesis step of an advisor council", "not valid json")
            .WhenUserContains("Skeptic body", "Persona view.");

        var orchestrator = BuildOrchestrator(registry, chat);
        var response = await orchestrator.RunAsync("Q?", "task-3", CancellationToken.None);

        Assert.AreEqual("low", response.Confidence);
        Assert.IsFalse(string.IsNullOrWhiteSpace(response.Synthesis));
    }

    [TestMethod]
    public async Task RunAsync_NoPersonasInRegistry_ReturnsEmptyResponse()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var registry = new PersonaRegistry(
            Options.Create(new CouncilOptions { PersonasPath = tmp }),
            NullLogger<PersonaRegistry>.Instance);
        var chat = new FakeChatClient();

        var orchestrator = BuildOrchestrator(registry, chat);
        var response = await orchestrator.RunAsync("anything", "task-4", CancellationToken.None);

        Assert.AreEqual(0, response.Personas.Count);
        Assert.AreEqual("low", response.Confidence);
    }

    private static CouncilOrchestrator BuildOrchestrator(PersonaRegistry registry, FakeChatClient chat)
    {
        var opts = Options.Create(new CouncilOptions { PerPersonaTimeoutSeconds = 30, OverallTimeoutSeconds = 60 });
        var wm = new InMemoryWorkingMemory();
        var select = new SelectStep(chat, registry, NullLogger<SelectStep>.Instance);
        // ResearchAgentInvoker passed as null! — the FakeChatClient never returns a tool-call response,
        // so PersonaStep's scoped research tool is never invoked. Same applies to PreResearchStep when
        // pre_research=false (the only case exercised by these orchestrator-shape tests).
        var persona = new PersonaStep(chat, invoker: null!, wm, opts, NullLogger<PersonaStep>.Instance);
        var critique = new CritiqueStep(chat, wm, NullLogger<CritiqueStep>.Instance);
        var synth = new SynthesizeStep(chat, NullLogger<SynthesizeStep>.Instance);
        var preResearch = new PreResearchStep(invoker: null!, wm, NullLogger<PreResearchStep>.Instance);
        return new CouncilOrchestrator(select, preResearch, persona, critique, synth, registry, opts,
            NullLogger<CouncilOrchestrator>.Instance);
    }
}
