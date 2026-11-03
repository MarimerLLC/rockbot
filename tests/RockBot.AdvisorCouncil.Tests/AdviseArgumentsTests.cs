using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>
/// The <c>advise</c> arguments (design phase 2): parsing, the Select step's overrides and
/// missing-field rules, the brief each step sees, and synthesis's <c>assumptions</c>.
/// </summary>
[TestClass]
public class AdviseArgumentsTests
{
    private static readonly HashSet<string> Known = new(["skeptic", "engineer", "economist"], StringComparer.OrdinalIgnoreCase);

    private static SelectorOutput Selected(IReadOnlyList<string>? missing = null, bool tooThin = false) =>
        new([new SelectedPersona("skeptic"), new SelectedPersona("engineer")], PreResearch: true, Critique: false, "r",
            missing, tooThin, tooThin ? ["the goal"] : null);

    [TestMethod]
    public void Create_NormalisesEnumsAndToggles_AndDropsBlanks()
    {
        var request = CouncilRequest.Create(
            "  Q?  ", context: "  ", options: ["A", " ", "a", "B"],
            timeHorizon: "Decade-Plus", riskAppetite: "huge", reversibility: "costly to reverse",
            personas: ["Skeptic"], critique: "OFF", preResearch: "maybe");

        Assert.AreEqual("Q?", request.Question);
        Assert.IsNull(request.Context);
        CollectionAssert.AreEqual(new[] { "A", "B" }, request.Options.ToArray());
        Assert.AreEqual("decade_plus", request.TimeHorizon);
        Assert.IsNull(request.RiskAppetite);
        Assert.AreEqual("costly_to_reverse", request.Reversibility);
        CollectionAssert.AreEqual(new[] { "skeptic" }, request.Personas.ToArray());
        Assert.AreEqual(CouncilToggle.Off, request.Critique);
        Assert.AreEqual(CouncilToggle.Auto, request.PreResearch);
    }

    [TestMethod]
    public void RenderBrief_IsTheBareQuestion_WhenNothingElseIsKnown()
    {
        Assert.AreEqual("Q?", new CouncilRequest("Q?").RenderBrief());
    }

    [TestMethod]
    public void RenderBrief_ListsWhatIsKnown()
    {
        var brief = CouncilRequest.Create("Q?", context: "Team of 4", options: ["A", "B"], timeHorizon: "years")
            with { PreferredOption = "B" };

        var text = brief.RenderBrief();

        StringAssert.StartsWith(text, "Q?");
        StringAssert.Contains(text, "Context: Team of 4");
        StringAssert.Contains(text, "Options being weighed: A; B");
        StringAssert.Contains(text, "Currently leaning toward: B");
        StringAssert.Contains(text, "Time horizon: years");
    }

    [TestMethod]
    public void ApplyRequest_ForcedPersonasAndToggles_OverrideTheSelector()
    {
        var request = CouncilRequest.Create("Q?", personas: ["economist", "nobody"], critique: "on", preResearch: "off");

        var applied = SelectStep.ApplyRequest(Selected(), request, Known);

        CollectionAssert.AreEqual(new[] { "economist" }, applied.Personas.Select(p => p.Id).ToArray(), "unknown ids are dropped");
        Assert.IsTrue(applied.Critique);
        Assert.IsFalse(applied.PreResearch);
    }

    [TestMethod]
    public void ApplyRequest_AnEmptyForcedSet_KeepsTheSelection()
    {
        var applied = SelectStep.ApplyRequest(Selected(), CouncilRequest.Create("Q?", personas: ["nobody"]), Known);

        Assert.AreEqual(2, applied.Personas.Count);
        Assert.IsTrue(applied.PreResearch, "auto keeps the selector's decision");
    }

    [TestMethod]
    public void ApplyRequest_MissingKeepsOnlyOpenAskableFields()
    {
        var request = CouncilRequest.Create("Q?", options: ["only one"], timeHorizon: "weeks");

        var applied = SelectStep.ApplyRequest(
            Selected(["time_horizon", "Risk_Appetite", "preferred_option", "budget", "risk_appetite"]), request, Known);

        CollectionAssert.AreEqual(new[] { "risk_appetite" }, applied.Missing!.ToArray(),
            "supplied, unknown, duplicate fields go, and preferred_option needs two options");
    }

    [TestMethod]
    public void ApplyRequest_TooThin_StandsWithoutContext_AndNotWithIt()
    {
        var thin = SelectStep.ApplyRequest(Selected(tooThin: true), new CouncilRequest("Do it?"), Known);
        Assert.IsTrue(thin.TooThin);
        CollectionAssert.AreEqual(new[] { "the goal" }, thin.Needs!.ToArray());

        var withContext = SelectStep.ApplyRequest(Selected(tooThin: true), CouncilRequest.Create("Do it?", context: "it = the migration"), Known);
        Assert.IsFalse(withContext.TooThin, "context is what too-thin asks for; never send the caller round again");
        Assert.AreEqual(0, withContext.Needs!.Count);
    }

    [TestMethod]
    public async Task SelectStep_ParsesMissingAndTooThin_AndSeesTheBrief()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "advise-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        foreach (var id in new[] { "skeptic", "engineer" })
            File.WriteAllText(Path.Combine(tmp, $"{id}.md"), $"---\nid: {id}\n---\n{id} body.");
        var registry = new PersonaRegistry(Options.Create(new CouncilOptions { PersonasPath = tmp }), NullLogger<PersonaRegistry>.Instance);
        var chat = new FakeChatClient().EnqueueResponse(
            """{ "personas": [{"id":"skeptic"}], "pre_research": false, "critique": false, "rationale": "x", "missing": ["risk_appetite"], "too_thin": false, "needs": [] }""");
        var step = new SelectStep(chat, registry, NullLogger<SelectStep>.Instance);

        var result = await step.RunAsync(CouncilRequest.Create("Q?", context: "Small team"), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "risk_appetite" }, result.Missing!.ToArray());
        Assert.IsFalse(result.TooThin);
        var user = chat.Calls[^1].Messages.Last(m => m.Role == ChatRole.User).Text ?? "";
        StringAssert.Contains(user, "Context: Small team");
    }

    [TestMethod]
    public async Task SynthesizeStep_ReturnsAssumptions_AndNamesTheOpenPoints()
    {
        var chat = new FakeChatClient().EnqueueResponse(
            """{ "synthesis": "Do it.", "confidence": "medium", "tensions": [], "assumptions": ["A multi-year horizon.", " ", 3] }""");
        var step = new SynthesizeStep(chat, NullLogger<SynthesizeStep>.Instance);
        var request = new CouncilRequest("Q?") { Unresolved = ["time_horizon"], Clarification = ClarificationStatus.Declined };

        var output = await step.RunAsync(
            new SynthesizeStep.SynthesisInput(request, [new PersonaView("skeptic", "view", [], [])], []), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "A multi-year horizon." }, output.Assumptions!.ToArray());
        var user = chat.Calls[^1].Messages.Last(m => m.Role == ChatRole.User).Text ?? "";
        StringAssert.Contains(user, "time horizon");
        StringAssert.Contains(user, "they were asked and did not answer");
    }

    [TestMethod]
    public void PersonaResearchContext_CarriesTheCallersContext()
    {
        var persona = new Persona("economist", "Economist", "d", "p", false);

        var context = PersonaStep.BuildResearchContext(persona, CouncilRequest.Create("Q?", context: "EU only"));

        StringAssert.Contains(context, "'economist' perspective");
        StringAssert.Contains(context, "The asker's context: EU only");
        Assert.IsFalse(PersonaStep.BuildResearchContext(persona, new CouncilRequest("Q?")).Contains("asker's context"));
    }

    [TestMethod]
    public void TheDefaults_ThinkOnHigh_AndTheTimeoutsFitTogether()
    {
        var o = new CouncilOptions();

        Assert.AreEqual(RockBot.Host.ModelTier.Balanced, o.SelectTier, "Select only routes");
        Assert.AreEqual(RockBot.Host.ModelTier.High, o.PersonaTier, "the personas' reasoning is the product");
        Assert.AreEqual(RockBot.Host.ModelTier.High, o.SynthesisTier);
        Assert.IsTrue(o.ResearchAgentTimeoutSeconds < o.PerPersonaTimeoutSeconds,
            "a persona that researches still has time to answer");
        Assert.IsTrue(o.PreResearchTimeoutSeconds + 2 * o.PerPersonaTimeoutSeconds < o.OverallTimeoutSeconds,
            "pre-research, the views and the critique fit inside the overall cap with room for synthesis");
    }

    [TestMethod]
    public void BuildText_PutsTheBannerFirstAndTheAssumptionsLast()
    {
        var response = new CouncilResponse(
            "Q?",
            [new PersonaView("skeptic", "v", [], []), new PersonaView("engineer", "(timed out)", [], [], PersonaStatus.TimedOut)],
            [], "The synthesis.", "low",
            new CouncilMetadata(false, false, 2, 1, 3, "h", "r"),
            ["Low risk appetite."]);

        var text = CouncilTools.BuildText(response);

        StringAssert.StartsWith(text, "> **Council coverage:** 1 of 2 personas contributed.");
        StringAssert.Contains(text, "engineer (timed_out)");
        Assert.IsTrue(text.IndexOf("The synthesis.", StringComparison.Ordinal) < text.IndexOf("**Assumptions**", StringComparison.Ordinal));
        StringAssert.EndsWith(text, "- Low risk appetite.");
    }
}
