using Microsoft.Extensions.Logging.Abstractions;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Schema;
using RockBot.AdvisorCouncil.Tools;

namespace RockBot.AdvisorCouncil.Tests;

/// <summary>Pre-research asks for facts, not a verdict, and stores them for the personas.</summary>
[TestClass]
public class PreResearchStepTests
{
    private sealed class Research(string answer) : IResearchClient
    {
        public List<(string Question, string? Context)> Calls { get; } = [];

        public Task<string> InvokeAsync(string question, string? context, CancellationToken ct, TimeSpan? timeout = null)
        {
            Calls.Add((question, context));
            return Task.FromResult(answer);
        }
    }

    [TestMethod]
    public async Task ItAsksForTheFactsBehindTheDecision_NotTheDecision()
    {
        var research = new Research("Studies found rents fell about 3% after an effective ban.");
        var wm = new InMemoryWorkingMemory();
        var step = new PreResearchStep(research, wm, NullLogger<PreResearchStep>.Instance);
        var personas = new[] { new Persona("economist", "Economist", "Incentives and costs", "p", true) };

        var ran = await step.RunAsync(
            CouncilRequest.Create("Should the city ban whole-home short-term rentals?", context: "City of 300,000"),
            personas, "run-p", CancellationToken.None);

        Assert.IsTrue(ran);
        var (question, context) = research.Calls.Single();
        StringAssert.StartsWith(question, "What facts and evidence bear on this decision");
        StringAssert.Contains(question, "not a recommendation");
        StringAssert.EndsWith(question, "Should the city ban whole-home short-term rentals?");
        StringAssert.Contains(context, "Report facts and evidence only");
        StringAssert.Contains(context, "Do not recommend a course of action");
        StringAssert.Contains(context, "- economist — Incentives and costs");
        StringAssert.Contains(context, "The asker's context: City of 300,000");
        Assert.AreEqual("Studies found rents fell about 3% after an effective ban.", await wm.GetAsync("council/run-p/shared"));
    }
}
