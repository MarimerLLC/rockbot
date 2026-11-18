namespace RockBot.Host.Tests;

/// <summary>
/// #846: per-run cost of scheduled tasks — the cost guard (a run far above the task's trailing
/// median is flagged) and the run history the dream's tier pass reads.
/// </summary>
[TestClass]
public sealed class ScheduledTaskRunCostsTests
{
    private string _dir = "";
    private readonly Dictionary<string, double> _ledger = new();
    private DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [TestInitialize]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), $"rb-runcosts-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ScheduledTaskRunCosts Create(ScheduledTaskCostOptions? options = null) =>
        new(task => _ledger.GetValueOrDefault(task), options ?? new ScheduledTaskCostOptions(),
            Path.Combine(_dir, "scheduled-task-runs.json"), null, () => _now);

    /// <summary>One run of <paramref name="task"/> costing <paramref name="cost"/>.</summary>
    private async Task<ScheduledRunCostCheck?> RunAsync(
        ScheduledTaskRunCosts costs, string task, double cost, ModelTier tier = ModelTier.Balanced)
    {
        costs.BeginRun(task, ModelTier.Balanced);
        if (tier != ModelTier.Balanced)
            costs.NoteTier(task, tier);
        _ledger[task] = _ledger.GetValueOrDefault(task) + cost;
        _now = _now.AddDays(7);
        return await costs.EndRunAsync(task);
    }

    [TestMethod]
    public async Task RunCost_IsTheLedgerDifferenceAcrossTheRun()
    {
        var costs = Create();
        _ledger["weekly"] = 40; // spend from before this run (earlier runs, other process lifetimes)

        var check = await RunAsync(costs, "weekly", 1.25);

        Assert.AreEqual(1.25, check!.CostUsd, 1e-9);
        var run = (await costs.GetRunsAsync("weekly")).Single();
        Assert.AreEqual(1.25, run.CostUsd, 1e-9);
        Assert.AreEqual(ModelTier.Balanced, run.HighestTier);
    }

    [TestMethod]
    public async Task ExpensiveRun_AfterABaseline_IsFlaggedWithANote()
    {
        var costs = Create();
        foreach (var cost in new[] { 1.10, 1.30, 1.20 })
            Assert.IsFalse((await RunAsync(costs, "weekly", cost))!.Anomalous);

        var check = await RunAsync(costs, "weekly", 12.74, ModelTier.High);

        Assert.IsTrue(check!.Anomalous);
        Assert.AreEqual(1.20, check.BaselineMedianUsd!.Value, 1e-9);
        Assert.AreEqual(3, check.BaselineRuns);
        StringAssert.Contains(check.Note, "$12.74");
        StringAssert.Contains(check.Note, "10.6×");
        StringAssert.Contains(check.Annotate("Report body.\n"), "Report body.\n\n---\n_Cost note:");

        var last = (await costs.GetRunsAsync("weekly"))[^1];
        Assert.IsTrue(last.Anomalous);
        Assert.AreEqual(ModelTier.High, last.HighestTier, "a subagent's High tier is the run's highest");
    }

    [TestMethod]
    public async Task NoFlag_WithTooFewRuns_OrBelowTheMultiplier_OrBelowTheFloor()
    {
        var costs = Create();
        Assert.IsFalse((await RunAsync(costs, "a", 1))!.Anomalous);
        Assert.IsFalse((await RunAsync(costs, "a", 50))!.Anomalous, "one prior run is no baseline");

        var cheap = Create(new ScheduledTaskCostOptions { MinBaselineRuns = 1 });
        foreach (var cost in new[] { 0.01, 0.01, 0.01 })
            await RunAsync(cheap, "tiny", cost);
        var check = await RunAsync(cheap, "tiny", 0.10);
        Assert.IsFalse(check!.Anomalous, "10x of a cent is still under the $0.25 floor");
        Assert.IsNull(check.Note);

        foreach (var cost in new[] { 1.0, 1.0, 1.0 })
            await RunAsync(cheap, "steady", cost);
        Assert.IsFalse((await RunAsync(cheap, "steady", 2.9))!.Anomalous, "2.9x is under the 3x multiplier");
    }

    [TestMethod]
    public async Task EndWithoutBegin_ReturnsNull_AndRecordsNothing()
    {
        var costs = Create();

        Assert.IsNull(await costs.EndRunAsync("never-started"));
        Assert.AreEqual(0, (await costs.GetRunsAsync("never-started")).Count);
    }

    [TestMethod]
    public async Task NoLedger_DoesNothing()
    {
        var costs = new ScheduledTaskRunCosts(null, new ScheduledTaskCostOptions(),
            Path.Combine(_dir, "runs.json"), null, () => _now);

        costs.BeginRun("t", ModelTier.Balanced);
        Assert.IsNull(await costs.EndRunAsync("t"));
    }

    [TestMethod]
    public async Task History_SurvivesARestart_AndIsCappedPerTask()
    {
        var costs = Create(new ScheduledTaskCostOptions { MaxRunsPerTask = 3 });
        foreach (var cost in new[] { 1.0, 2.0, 3.0, 4.0 })
            await RunAsync(costs, "weekly", cost);

        var reloaded = Create();
        var runs = await reloaded.GetRunsAsync("weekly");
        CollectionAssert.AreEqual(new[] { 2.0, 3.0, 4.0 }, runs.Select(r => r.CostUsd).ToArray());

        var summary = (await reloaded.GetSummariesAsync()).Single();
        Assert.AreEqual("weekly", summary.TaskName);
        Assert.AreEqual(3, summary.Runs);
        Assert.AreEqual(4.0, summary.LastRun.CostUsd, 1e-9);
        Assert.AreEqual(3.0, summary.MedianCostUsd!.Value, 1e-9);
    }

    [TestMethod]
    public void Median_OddEvenEmpty()
    {
        Assert.AreEqual(2.0, ScheduledTaskRunCosts.Median([3, 1, 2]));
        Assert.AreEqual(2.5, ScheduledTaskRunCosts.Median([4, 1, 2, 3]));
        Assert.IsNull(ScheduledTaskRunCosts.Median([]));
    }
}

/// <summary>#846: when the dream caps a recurring task at Balanced.</summary>
[TestClass]
public sealed class ScheduledTaskTierAdvisorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 3, 0, 0, TimeSpan.Zero);
    private static readonly ScheduledTaskCostOptions Options = new();

    private static ScheduledTask Task(string name = "weekly") => new(name, "0 7 * * 1", "sweep mail", Now.AddDays(-60));

    private static List<ScheduledRunRecord> Runs(params (double Cost, ModelTier? Tier)[] runs) =>
        [.. runs.Select((r, i) => new ScheduledRunRecord(Now.AddDays(-7 * (runs.Length - i)), Now.AddDays(-7 * (runs.Length - i)), r.Cost, r.Tier))];

    [TestMethod]
    public void HighRunsCostingFarMoreThanBalanced_CapsAtBalanced()
    {
        var runs = Runs((1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced), (1.2, ModelTier.Balanced), (12.74, ModelTier.High));

        var advice = ScheduledTaskTierAdvisor.Advise(Task(), runs, Options, Now);

        Assert.AreEqual(ModelTier.Balanced, advice!.Value.MaxTier);
        StringAssert.Contains(advice.Value.Reason, "1 of its last 4 runs used High");
        StringAssert.Contains(advice.Value.Reason, "2026-11-17");
    }

    [TestMethod]
    public void LeavesAlone_AlreadyCapped_RunOnce_FewBalancedRuns_NoHighRuns_OrSmallGap()
    {
        var costly = Runs((1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced), (1.2, ModelTier.Balanced), (12.74, ModelTier.High));

        Assert.IsNull(ScheduledTaskTierAdvisor.Advise(Task() with { MaxTier = ModelTier.High }, costly, Options, Now),
            "a ceiling already set (here, deliberately High) is never replaced");
        Assert.IsNull(ScheduledTaskTierAdvisor.Advise(Task() with { RunOnce = true }, costly, Options, Now));
        Assert.IsNull(ScheduledTaskTierAdvisor.Advise(Task(),
            Runs((1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced), (12.74, ModelTier.High)), Options, Now),
            "two Balanced runs are not enough evidence Balanced does the job");
        Assert.IsNull(ScheduledTaskTierAdvisor.Advise(Task(),
            Runs((1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced), (1.2, ModelTier.Balanced), (1.4, ModelTier.Balanced)), Options, Now));
        Assert.IsNull(ScheduledTaskTierAdvisor.Advise(Task(),
            Runs((1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced), (1.2, ModelTier.Balanced), (2.0, ModelTier.High)), Options, Now),
            "High at 1.7x Balanced is under the 2x ratio");
        Assert.IsNull(ScheduledTaskTierAdvisor.Advise(Task(),
            Runs((1.1, null), (1.3, null), (1.2, null), (12.74, ModelTier.High)), Options, Now),
            "runs of unknown tier are not evidence");
    }

    [TestMethod]
    public void OnlyTheLatestRunsCount()
    {
        // An old High spike outside the baseline window doesn't cap a task that has run Balanced since.
        var runs = Runs((12.74, ModelTier.High),
            (1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced), (1.2, ModelTier.Balanced),
            (1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced), (1.2, ModelTier.Balanced),
            (1.1, ModelTier.Balanced), (1.3, ModelTier.Balanced));

        Assert.IsNull(ScheduledTaskTierAdvisor.Advise(Task(), runs, Options, Now));
    }
}
