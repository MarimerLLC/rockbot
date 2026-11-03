using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;
using RockBot.Llm;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// The council's two decision-model uses: skipping critique when the views already agree
/// (<see cref="CritiqueGate"/>) and reusing research the council already holds
/// (<see cref="ResearchReuse"/>).
/// </summary>
[TestClass]
public class CouncilDecisionsTests
{
    /// <summary>Answers each noul question from <c>answers</c> (by name, else <c>fallback</c>); null fails the call.</summary>
    private sealed class Decisions(IReadOnlyDictionary<string, double>? answers = null, double? fallback = null, bool enabled = true)
        : IDecisionService
    {
        public List<DecisionRequest> Requests { get; } = [];
        public double? Threshold { get; init; }

        public bool IsEnabled(string purpose) => enabled;

        public double ThresholdFor(string purpose, double fallbackThreshold) => Threshold ?? fallbackThreshold;

        public Task<DecisionResult?> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests) Requests.Add(request);
            if (answers is null && fallback is null)
                return Task.FromResult<DecisionResult?>(null);
            var result = request.Questions.Keys.ToDictionary(
                k => k,
                k => new DecisionAnswer(Probability: answers is not null && answers.TryGetValue(k, out var p) ? p : fallback ?? 0));
            return Task.FromResult<DecisionResult?>(new DecisionResult(result));
        }
    }

    private static PersonaView View(string id, string text = "a view", string status = PersonaStatus.Ok) => new(id, text, [], [], status);

    private static readonly PersonaView[] TwoViews = [View("skeptic", "Wait a year."), View("engineer", "Wait a year too.")];

    // ── CritiqueGate ─────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(0.9, true)]
    [DataRow(0.35, true)]
    [DataRow(0.2, false)]
    public async Task Critique_RunsUnlessTheModelIsFairlySureTheViewsAgree(double pDisagree, bool expected)
    {
        var gate = new CritiqueGate(new Decisions(fallback: pDisagree));

        Assert.AreEqual(expected, await gate.ShouldCritiqueAsync(new CouncilRequest("Q?"), TwoViews, CancellationToken.None));
    }

    [TestMethod]
    public async Task Critique_RunsWhenTheModelIsOffOrFails()
    {
        Assert.IsTrue(await new CritiqueGate().ShouldCritiqueAsync(new CouncilRequest("Q?"), TwoViews, CancellationToken.None));
        Assert.IsTrue(await new CritiqueGate(new Decisions(enabled: false, fallback: 0))
            .ShouldCritiqueAsync(new CouncilRequest("Q?"), TwoViews, CancellationToken.None));
        Assert.IsTrue(await new CritiqueGate(new Decisions())
            .ShouldCritiqueAsync(new CouncilRequest("Q?"), TwoViews, CancellationToken.None), "a failed call keeps critique");
    }

    [TestMethod]
    public async Task Critique_OnlyContributingViewsAreCompared()
    {
        var decisions = new Decisions(fallback: 0.0);
        var gate = new CritiqueGate(decisions);

        var run = await gate.ShouldCritiqueAsync(new CouncilRequest("Q?"),
            [View("skeptic"), View("engineer", "(timed out)", PersonaStatus.TimedOut)], CancellationToken.None);

        Assert.IsTrue(run, "one real view: nothing to compare, the gate stays out of it");
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    public void Critique_TheRequestCarriesTheBriefAndTheViews()
    {
        var request = CritiqueGate.BuildRequest(
            CouncilRequest.Create("Adopt Rust?", context: "Team of 4"), TwoViews);

        Assert.AreEqual(DecisionPurposes.CouncilCritique, request.Purpose);
        var state = (IDictionary<string, object?>)request.State;
        StringAssert.Contains((string)state["question"]!, "Context: Team of 4");
        Assert.AreEqual(2, ((System.Collections.ICollection)state["advisor_views"]!).Count);
    }

    [TestMethod]
    public async Task TheOrchestrator_SkipsCritiqueWhenTheViewsAgree_AndSaysSo()
    {
        var chat = CritiqueChat();
        var orchestrator = BuildOrchestrator(chat, new CritiqueGate(new Decisions(fallback: 0.1)));

        var response = await orchestrator.RunAsync(new CouncilRequest("Should we wait?"), "run-1", CancellationToken.None);

        Assert.IsFalse(response.Metadata.CritiqueRun);
        Assert.IsTrue(response.Metadata.CritiqueSkipped);
        Assert.IsFalse(chat.Calls.Any(c => c.Messages.Any(m => m.Text?.Contains("Critique addendum") == true)),
            "no critique calls were made");
    }

    [TestMethod]
    public async Task TheOrchestrator_ACallerWhoAskedForCritique_GetsIt()
    {
        var decisions = new Decisions(fallback: 0.1);
        var chat = CritiqueChat();
        var orchestrator = BuildOrchestrator(chat, new CritiqueGate(decisions));

        var response = await orchestrator.RunAsync(CouncilRequest.Create("Should we wait?", critique: "on"), "run-2", CancellationToken.None);

        Assert.IsTrue(response.Metadata.CritiqueRun);
        Assert.IsFalse(response.Metadata.CritiqueSkipped);
        Assert.AreEqual(0, decisions.Requests.Count, "critique: on never asks");
    }

    [TestMethod]
    public async Task TheOrchestrator_DisagreeingViews_StillGetCritique()
    {
        var orchestrator = BuildOrchestrator(CritiqueChat(), new CritiqueGate(new Decisions(fallback: 0.8)));

        var response = await orchestrator.RunAsync(new CouncilRequest("Should we wait?"), "run-3", CancellationToken.None);

        Assert.IsTrue(response.Metadata.CritiqueRun);
        Assert.IsFalse(response.Metadata.CritiqueSkipped);
    }

    private static FakeChatClient CritiqueChat() => new FakeChatClient()
        .WhenUserContains("selector for an advisor council",
            """{ "personas": [{"id":"skeptic"},{"id":"engineer"}], "pre_research": false, "critique": true, "rationale": "Contested." }""")
        .WhenUserContains("synthesis step of an advisor council",
            """{ "synthesis": "Wait.", "confidence": "high", "tensions": [], "assumptions": [] }""")
        .WhenUserContains("Critique addendum",
            """{ "revised_view": "(revised)", "key_points": [], "tensions": [] }""")
        .WhenUserContains("Skeptic body", "Wait a year.")
        .WhenUserContains("Engineer body", "Wait a year too.");

    private static CouncilOrchestrator BuildOrchestrator(FakeChatClient chat, CritiqueGate gate)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "decisions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        foreach (var (id, body) in new[] { ("skeptic", "Skeptic body"), ("engineer", "Engineer body") })
            File.WriteAllText(Path.Combine(tmp, $"{id}.md"), $"---\nid: {id}\n---\n{body}");
        var registry = new PersonaRegistry(Options.Create(new CouncilOptions { PersonasPath = tmp }), NullLogger<PersonaRegistry>.Instance);

        var opts = Options.Create(new CouncilOptions { PerPersonaTimeoutSeconds = 30, OverallTimeoutSeconds = 60 });
        var wm = new InMemoryWorkingMemory();
        return new CouncilOrchestrator(
            new SelectStep(chat, registry, NullLogger<SelectStep>.Instance),
            new PreResearchStep(research: null!, wm, NullLogger<PreResearchStep>.Instance),
            new PersonaStep(chat, research: null!, wm, opts, NullLogger<PersonaStep>.Instance),
            new CritiqueStep(chat, wm, NullLogger<CritiqueStep>.Instance),
            new SynthesizeStep(chat, NullLogger<SynthesizeStep>.Instance),
            registry, opts, NullLogger<CouncilOrchestrator>.Instance, wm, gate);
    }

    // ── ResearchReuse ────────────────────────────────────────────────────────

    private static async Task<InMemoryWorkingMemory> CouncilMemoryAsync()
    {
        var wm = new InMemoryWorkingMemory();
        await wm.SetAsync("council/run-r/shared", "Deer density in the eastern US is 30-45 per square mile.");
        await Task.Delay(20); // distinct StoredAt: candidates are ordered newest first
        await wm.SetAsync("council/run-r/engineer/research/1", "Q: CWD prevalence?\n\nCWD is in 33 states.");
        await wm.SetAsync("council/run-r/engineer/view", "A view, not research.");
        await wm.SetAsync("council/other-run/shared", "Another council's research.");
        return wm;
    }

    [TestMethod]
    public async Task Reuse_ACoveringFinding_IsReturnedInPlaceOfANewCall()
    {
        var wm = await CouncilMemoryAsync();
        // Candidates are newest first: research/1 is finding_1, shared is finding_2.
        var reuse = new ResearchReuse(wm, new Decisions(new Dictionary<string, double>
        {
            [ResearchReuse.CandidateName(0)] = 0.92,
            [ResearchReuse.CandidateName(1)] = 0.30,
        }));

        var found = await reuse.FindAsync("How widespread is chronic wasting disease?", "run-r", CancellationToken.None);

        Assert.IsNotNull(found);
        StringAssert.StartsWith(found, ResearchReuse.ReusedPrefix);
        StringAssert.Contains(found, "CWD is in 33 states.");
    }

    [TestMethod]
    public async Task Reuse_BelowTheBar_ResearchesAfterAll()
    {
        var reuse = new ResearchReuse(await CouncilMemoryAsync(), new Decisions(fallback: 0.6));

        Assert.IsNull(await reuse.FindAsync("Hunter numbers since 1980?", "run-r", CancellationToken.None));
    }

    [TestMethod]
    public async Task Reuse_ConsidersOnlyThisRunsResearch()
    {
        var decisions = new Decisions(fallback: 0.0);
        var reuse = new ResearchReuse(await CouncilMemoryAsync(), decisions);

        await reuse.FindAsync("Anything?", "run-r", CancellationToken.None);

        var request = decisions.Requests.Single();
        Assert.AreEqual(DecisionPurposes.CouncilResearchReuse, request.Purpose);
        Assert.AreEqual(2, request.Questions.Count, "the shared finding and the persona's research; not the view, not the other run");
        var findings = (System.Collections.IEnumerable)((IDictionary<string, object?>)request.State)["findings"]!;
        var texts = findings.Cast<IDictionary<string, object?>>().Select(f => (string)f["text"]!).ToList();
        Assert.IsFalse(texts.Any(t => t.Contains("Another council") || t.Contains("not research")));
    }

    [TestMethod]
    public async Task Reuse_NothingHeldYet_AsksNothing()
    {
        var decisions = new Decisions(fallback: 0.99);
        var reuse = new ResearchReuse(new InMemoryWorkingMemory(), decisions);

        Assert.IsNull(await reuse.FindAsync("Q?", "run-empty", CancellationToken.None));
        Assert.AreEqual(0, decisions.Requests.Count);
    }

    [TestMethod]
    public async Task Reuse_OffOrFailing_ResearchesAsBefore()
    {
        var wm = await CouncilMemoryAsync();
        Assert.IsFalse(new ResearchReuse(wm).IsEnabled);
        Assert.IsNull(await new ResearchReuse(wm, new Decisions(fallback: 0.99, enabled: false)).FindAsync("Q?", "run-r", CancellationToken.None));
        Assert.IsNull(await new ResearchReuse(wm, new Decisions()).FindAsync("Q?", "run-r", CancellationToken.None), "a failed call");
    }

    [TestMethod]
    public async Task TheResearchTool_UsesAReusedFinding_WithoutSpendingBudgetOrCallingResearch()
    {
        var wm = new InMemoryWorkingMemory();
        var researchCalls = 0;
        var tool = new PersonaStep.PersonaScopedResearchTool(
            research: (q, _) => { Interlocked.Increment(ref researchCalls); return Task.FromResult($"fresh: {q}"); },
            wm: wm, taskId: "run-t", personaId: "economist", maxCalls: 1, logger: NullLogger.Instance,
            reuse: (q, _) => Task.FromResult(q.Contains("known") ? $"{ResearchReuse.ReusedPrefix}\n\nold findings" : null));

        var reused = (string?)await tool.InvokeAsync(new AIFunctionArguments { ["question"] = "known thing" });
        var fresh = (string?)await tool.InvokeAsync(new AIFunctionArguments { ["question"] = "new thing" });

        StringAssert.Contains(reused!, "old findings");
        Assert.AreEqual("fresh: new thing", fresh, "the reuse didn't use the one-call budget");
        Assert.AreEqual(1, researchCalls);
        Assert.AreEqual(1, wm.Entries.Count, "only the fresh research is written; the reused one is already held");
    }
}
