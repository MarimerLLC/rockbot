using Microsoft.Extensions.AI;
using RockBot.Host;

namespace RockBot.ResearchAgent.Tests;

[TestClass]
public class ResearchDepthTests
{
    /// <summary>A clock a test moves by hand.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private static AIFunction Probe(List<int> calls) =>
        AIFunctionFactory.Create((int x) => { lock (calls) calls.Add(x); return "ok"; }, "probe");

    [TestMethod]
    [DataRow("quick", "Quick")]
    [DataRow(" Quick ", "Quick")]
    [DataRow("thorough", "Thorough")]
    [DataRow(null, "Thorough")]
    [DataRow("deep", "Thorough")]
    public void Parse_AnythingButQuick_IsThorough(string? value, string expected) =>
        Assert.AreEqual(expected, ResearchDepthProfile.Parse(value).ToString());

    [TestMethod]
    public void Profiles_BothRunOnHigh_QuickIsBounded_ThoroughIsUnbounded()
    {
        var quick = ResearchDepthProfile.For(ResearchDepth.Quick);
        Assert.AreEqual(ModelTier.High, quick.Tier, "depth bounds the work, not the model");
        Assert.AreEqual(ResearchDepthProfile.QuickMaxToolCalls, quick.MaxToolCalls);
        Assert.IsNotNull(quick.TimeBox);
        Assert.IsNotNull(quick.PromptNote);

        var thorough = ResearchDepthProfile.For(ResearchDepth.Thorough);
        Assert.AreEqual(ModelTier.High, thorough.Tier, "thorough research is unchanged");
        Assert.IsNull(thorough.MaxToolCalls);
        Assert.IsNull(thorough.TimeBox);
        Assert.IsNull(thorough.PromptNote);
    }

    [TestMethod]
    public async Task TheBudget_RunsCallsUntilSpent_ThenTellsTheModelToAnswer()
    {
        var calls = new List<int>();
        var budget = new ResearchToolBudget(maxCalls: 2, timeBox: TimeSpan.FromMinutes(1));
        var tool = (AIFunction)budget.Wrap([Probe(calls)]).Single();

        var results = new List<object?>();
        for (var i = 0; i < 4; i++)
            results.Add(await tool.InvokeAsync(new AIFunctionArguments { ["x"] = i }));

        CollectionAssert.AreEqual(new[] { 0, 1 }, calls, "only the budgeted calls run");
        Assert.AreEqual(ResearchToolBudget.ExhaustedNote, results[2]);
        Assert.AreEqual(ResearchToolBudget.ExhaustedNote, results[3]);
        Assert.AreEqual("probe", tool.Name, "the wrapped tool keeps its name and schema");
    }

    [TestMethod]
    public async Task TheTimeBox_EndsTheBudgetEvenWithCallsLeft()
    {
        var time = new ManualTime();
        var calls = new List<int>();
        var budget = new ResearchToolBudget(maxCalls: 10, timeBox: TimeSpan.FromSeconds(60), time);
        var tool = (AIFunction)budget.Wrap([Probe(calls)]).Single();

        await tool.InvokeAsync(new AIFunctionArguments { ["x"] = 1 });
        time.Advance(TimeSpan.FromSeconds(61));
        var late = await tool.InvokeAsync(new AIFunctionArguments { ["x"] = 2 });

        CollectionAssert.AreEqual(new[] { 1 }, calls);
        Assert.AreEqual(ResearchToolBudget.ExhaustedNote, late);
    }

    [TestMethod]
    public async Task ACallStillRunningWhenTheTimeBoxEnds_IsCutOff()
    {
        var hung = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // a page that never answers
            return "page";
        }, "web_browse");
        var budget = new ResearchToolBudget(maxCalls: 8, timeBox: TimeSpan.FromMilliseconds(300));
        var tool = (AIFunction)budget.Wrap([hung]).Single();

        var started = DateTime.UtcNow;
        var result = await tool.InvokeAsync(new AIFunctionArguments());

        Assert.AreEqual(ResearchToolBudget.CutOffNote, result);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "cut off at the box, not after the page");
        Assert.AreEqual(ResearchToolBudget.ExhaustedNote, await tool.InvokeAsync(new AIFunctionArguments()),
            "the box is spent: the next call doesn't run");
    }

    [TestMethod]
    public async Task AToolThatSwallowsItsCancellation_StillGivesTheModelTheCutOffNote()
    {
        // WebBrowseToolExecutor catches every exception and returns its own error text.
        var swallowing = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); return "page"; }
            catch (OperationCanceledException) { return "Error fetching page: A task was canceled."; }
        }, "web_browse");
        var budget = new ResearchToolBudget(maxCalls: 8, timeBox: TimeSpan.FromMilliseconds(300));
        var tool = (AIFunction)budget.Wrap([swallowing]).Single();

        Assert.AreEqual(ResearchToolBudget.CutOffNote, await tool.InvokeAsync(new AIFunctionArguments()));
    }

    [TestMethod]
    public async Task ACallThatFinishesInTime_KeepsItsResult()
    {
        var calls = new List<int>();
        var budget = new ResearchToolBudget(maxCalls: 8, timeBox: TimeSpan.FromSeconds(30));
        var tool = (AIFunction)budget.Wrap([Probe(calls)]).Single();

        Assert.AreEqual("ok", (await tool.InvokeAsync(new AIFunctionArguments { ["x"] = 1 }))?.ToString());
    }

    [TestMethod]
    public async Task TheCallersCancellation_IsNotMistakenForTheTimeBox()
    {
        var hung = AIFunctionFactory.Create(async (CancellationToken ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "page";
        }, "web_browse");
        var budget = new ResearchToolBudget(maxCalls: 8, timeBox: TimeSpan.FromSeconds(30));
        var tool = (AIFunction)budget.Wrap([hung]).Single();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () => await tool.InvokeAsync(new AIFunctionArguments(), cts.Token));
    }

    [TestMethod]
    public async Task TheToolsDepthArgument_ReachesTheRunner()
    {
        var runner = new FakeResearchRunner();
        await using var server = await ResearchServerHarness.StartAsync(runner, FakeScoper.Clear());
        var client = await server.ConnectAsync();

        await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "a", ["depth"] = "quick" });
        await client.CallToolAsync(ResearchTools.ToolName, new Dictionary<string, object?> { ["question"] = "b" });

        var byQuestion = runner.Requests.ToDictionary(r => r.Question);
        Assert.AreEqual(ResearchDepth.Quick, byQuestion["a"].Depth);
        Assert.AreEqual(ResearchDepth.Thorough, byQuestion["b"].Depth);
    }
}
