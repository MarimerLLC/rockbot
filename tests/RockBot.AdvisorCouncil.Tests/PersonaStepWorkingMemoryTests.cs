using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// Behaviour tests for the WM-mediated parts of the council pipeline:
/// shared pre-research read-through, per-persona view write, scoped research tool
/// (WM write + budget enforcement), and CritiqueStep's WM pool inclusion.
/// </summary>
[TestClass]
public class PersonaStepWorkingMemoryTests
{
    private static Persona MakePersona(string id = "skeptic") => new(
        Id: id,
        Name: id,
        Description: $"{id} description",
        SystemPrompt: $"{id} system prompt",
        DefaultResearch: false);

    [TestMethod]
    public async Task PersonaStep_PrependsSharedPreResearchToPrompt_WhenWmHasShared()
    {
        var wm = new InMemoryWorkingMemory();
        await wm.SetAsync("council/task-99/shared", "Pre-existing baseline findings about X.");

        var chat = new FakeChatClient().EnqueueResponse("persona response text");
        var opts = Options.Create(new CouncilOptions());
        var step = new PersonaStep(chat, invoker: null!, wm, opts, NullLogger<PersonaStep>.Instance);

        var view = await step.RunAsync(MakePersona(), "Original question?", "task-99", CancellationToken.None);

        Assert.AreEqual("persona response text", view.View);
        var lastCall = chat.Calls[^1];
        var userMessage = lastCall.Messages.Last(m => m.Role == ChatRole.User).Text ?? string.Empty;
        StringAssert.Contains(userMessage, "Pre-existing baseline findings about X.");
        StringAssert.Contains(userMessage, "Original question?");
        StringAssert.Contains(userMessage, "Pre-research findings");
    }

    [TestMethod]
    public async Task PersonaStep_WritesViewToWm_AtPersonaViewKey()
    {
        var wm = new InMemoryWorkingMemory();
        var chat = new FakeChatClient().EnqueueResponse("My view as the skeptic.");
        var opts = Options.Create(new CouncilOptions());
        var step = new PersonaStep(chat, invoker: null!, wm, opts, NullLogger<PersonaStep>.Instance);

        await step.RunAsync(MakePersona("skeptic"), "Q?", "task-7", CancellationToken.None);

        var stored = await wm.GetAsync("council/task-7/skeptic/view");
        Assert.AreEqual("My view as the skeptic.", stored);
    }

    [TestMethod]
    public async Task PersonaStep_NoSharedInWm_UsesRawQuestionAsPrompt()
    {
        var wm = new InMemoryWorkingMemory();
        var chat = new FakeChatClient().EnqueueResponse("view");
        var opts = Options.Create(new CouncilOptions());
        var step = new PersonaStep(chat, invoker: null!, wm, opts, NullLogger<PersonaStep>.Instance);

        await step.RunAsync(MakePersona(), "Just the question.", "task-x", CancellationToken.None);

        var userMessage = chat.Calls[^1].Messages.Last(m => m.Role == ChatRole.User).Text ?? string.Empty;
        Assert.AreEqual("Just the question.", userMessage);
    }

    [TestMethod]
    public async Task ScopedResearchTool_OnSuccess_WritesFindingsToWm_WithIncrementingIndex()
    {
        var wm = new InMemoryWorkingMemory();
        var tool = new PersonaStep.PersonaScopedResearchTool(
            research: (q, _) => Task.FromResult($"answer for: {q}"),
            wm: wm,
            taskId: "task-a",
            personaId: "engineer",
            maxCalls: 3,
            logger: NullLogger.Instance);

        await InvokeToolAsync(tool, "first question");
        await InvokeToolAsync(tool, "second question");

        var first = await wm.GetAsync("council/task-a/engineer/research/1");
        var second = await wm.GetAsync("council/task-a/engineer/research/2");
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        StringAssert.Contains(first!, "first question");
        StringAssert.Contains(first!, "answer for: first question");
        StringAssert.Contains(second!, "second question");
    }

    [TestMethod]
    public async Task ScopedResearchTool_PastBudget_ReturnsSentinel_AndDoesNotWriteToWm()
    {
        var wm = new InMemoryWorkingMemory();
        var underlyingCalls = 0;
        var tool = new PersonaStep.PersonaScopedResearchTool(
            research: (q, _) =>
            {
                Interlocked.Increment(ref underlyingCalls);
                return Task.FromResult($"answer-{q}");
            },
            wm: wm,
            taskId: "task-b",
            personaId: "engineer",
            maxCalls: 3,
            logger: NullLogger.Instance);

        var r1 = (string?)await InvokeToolAsync(tool, "q1");
        var r2 = (string?)await InvokeToolAsync(tool, "q2");
        var r3 = (string?)await InvokeToolAsync(tool, "q3");
        var r4 = (string?)await InvokeToolAsync(tool, "q4");

        StringAssert.Contains(r1!, "answer-q1");
        StringAssert.Contains(r3!, "answer-q3");
        StringAssert.Contains(r4!, "research budget exhausted");
        Assert.AreEqual(3, underlyingCalls, "Underlying research delegate should not be invoked past the cap");
        Assert.IsNull(await wm.GetAsync("council/task-b/engineer/research/4"));
    }

    [TestMethod]
    public async Task ScopedResearchTool_DoesNotWriteFailureSentinelsToWm()
    {
        var wm = new InMemoryWorkingMemory();
        var tool = new PersonaStep.PersonaScopedResearchTool(
            research: (_, _) => Task.FromResult("(research failed: simulated)"),
            wm: wm,
            taskId: "task-c",
            personaId: "skeptic",
            maxCalls: 3,
            logger: NullLogger.Instance);

        await InvokeToolAsync(tool, "any");

        Assert.IsNull(await wm.GetAsync("council/task-c/skeptic/research/1"));
    }

    [TestMethod]
    public async Task CritiqueStep_IncludesResearchPoolFromWm_AndExcludesViewKeys()
    {
        var wm = new InMemoryWorkingMemory();
        const string taskId = "task-crit";

        // Populate WM as the orchestrator would after pre-research + persona views ran.
        await wm.SetAsync($"council/{taskId}/shared", "Shared baseline content.");
        await wm.SetAsync($"council/{taskId}/engineer/research/1", "Q: feasibility?\n\nFeasibility looks tight.");
        await wm.SetAsync($"council/{taskId}/skeptic/view", "Skeptic view text.");
        await wm.SetAsync($"council/{taskId}/skeptic/view-revised", "Skeptic revised text.");

        var chat = new FakeChatClient().EnqueueResponse(
            """{ "revised_view": "revised", "key_points": [], "tensions": [] }""");
        var step = new CritiqueStep(chat, wm, NullLogger<CritiqueStep>.Instance);

        var own = new RockBot.AdvisorCouncil.Schema.PersonaView("skeptic", "Skeptic view text.", [], []);
        var siblings = new[] { new RockBot.AdvisorCouncil.Schema.PersonaView("engineer", "Engineer view.", [], []) };

        await step.RunAsync(MakePersona("skeptic"), "Question?", taskId, own, siblings, CancellationToken.None);

        var userMessage = chat.Calls[^1].Messages.Last(m => m.Role == ChatRole.User).Text ?? string.Empty;
        StringAssert.Contains(userMessage, "Research findings available to the council");
        StringAssert.Contains(userMessage, "Shared baseline content.");
        StringAssert.Contains(userMessage, "Feasibility looks tight.");
        Assert.IsFalse(userMessage.Contains("Skeptic revised text.", StringComparison.Ordinal),
            "view-revised entries must not be included in the research pool section");
    }

    [TestMethod]
    public async Task CritiqueStep_WritesRevisedViewToWm()
    {
        var wm = new InMemoryWorkingMemory();
        const string taskId = "task-revised";
        var chat = new FakeChatClient().EnqueueResponse(
            """{ "revised_view": "post-rebuttal text", "key_points": [], "tensions": [] }""");
        var step = new CritiqueStep(chat, wm, NullLogger<CritiqueStep>.Instance);

        var own = new RockBot.AdvisorCouncil.Schema.PersonaView("skeptic", "before", [], []);
        var siblings = new[] { new RockBot.AdvisorCouncil.Schema.PersonaView("engineer", "engineer view", [], []) };

        await step.RunAsync(MakePersona("skeptic"), "Q?", taskId, own, siblings, CancellationToken.None);

        var stored = await wm.GetAsync($"council/{taskId}/skeptic/view-revised");
        Assert.AreEqual("post-rebuttal text", stored);
    }

    private static async Task<object?> InvokeToolAsync(AIFunction tool, string question)
    {
        var args = new AIFunctionArguments { ["question"] = question };
        return await tool.InvokeAsync(args, CancellationToken.None);
    }
}
