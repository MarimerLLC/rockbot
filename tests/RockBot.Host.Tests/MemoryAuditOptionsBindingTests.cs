using Microsoft.Extensions.Configuration;

namespace RockBot.Host.Tests;

/// <summary>
/// Guards the wiring that carries the Helm ConfigMap's <c>MemoryAudit__*</c> keys into
/// <see cref="MemoryAuditOptions"/>. Bound against the exact string shapes the ConfigMap emits —
/// in particular the TimeSpan "d.hh:mm:ss" form, which silently falls back to the default when
/// the binder cannot parse it.
/// </summary>
[TestClass]
public class MemoryAuditOptionsBindingTests
{
    private static MemoryAuditOptions Bind(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var opts = new MemoryAuditOptions();
        config.GetSection("MemoryAudit").Bind(opts);
        return opts;
    }

    [TestMethod]
    public void BindsWhatTheConfigMapActuallyEmits()
    {
        var opts = Bind(new Dictionary<string, string?>
        {
            ["MemoryAudit:Enabled"] = "true",
            ["MemoryAudit:CronSchedule"] = "0 4 * * *",
            ["MemoryAudit:BasePath"] = "/data/agent/memory-audit",
            ["MemoryAudit:SharedReportDirectory"] = "/rockbot/shared/exports/memory-audit",
            ["MemoryAudit:PauseConsolidationOnAlert"] = "false",
            ["MemoryAudit:EvalEnabled"] = "true",
            ["MemoryAudit:EvalCronSchedule"] = "0 5 * * 0",
        });

        Assert.IsTrue(opts.Enabled);
        Assert.AreEqual("0 4 * * *", opts.CronSchedule);
        Assert.AreEqual("/data/agent/memory-audit", opts.BasePath);
        Assert.AreEqual("/rockbot/shared/exports/memory-audit", opts.SharedReportDirectory);
        Assert.IsFalse(opts.PauseConsolidationOnAlert);
        Assert.IsTrue(opts.EvalEnabled);
        Assert.AreEqual("0 5 * * 0", opts.EvalCronSchedule);
    }

    [TestMethod]
    public void BindsTimeSpansFromTheConfigMapStringShapes()
    {
        var opts = Bind(new Dictionary<string, string?>
        {
            ["MemoryAudit:SnapshotRetention"] = "400.00:00:00",
            ["MemoryAudit:EvalWindow"] = "14.00:00:00",
            ["MemoryAudit:InitialDelay"] = "00:10:00",
        });

        Assert.AreEqual(TimeSpan.FromDays(400), opts.SnapshotRetention);
        Assert.AreEqual(TimeSpan.FromDays(14), opts.EvalWindow);
        Assert.AreEqual(TimeSpan.FromMinutes(10), opts.InitialDelay);
    }

    [TestMethod]
    public void BindsThresholdsIncludingTheZeroThatMeansNoTolerance()
    {
        var opts = Bind(new Dictionary<string, string?>
        {
            ["MemoryAudit:MaxNetGrowthPerDay"] = "12.5",
            ["MemoryAudit:MaxMergeChainDepth"] = "3",
            ["MemoryAudit:MaxRejectedMergesPerWeek"] = "20",
            ["MemoryAudit:MaxHardDeletesOutsidePurge"] = "0",
            ["MemoryAudit:MaxLossPercentBetweenSnapshots"] = "25",
            ["MemoryAudit:RepeatedRejectionRuns"] = "5",
            ["MemoryAudit:NearDuplicateThreshold"] = "0.45",
        });

        Assert.AreEqual(12.5, opts.MaxNetGrowthPerDay);
        Assert.AreEqual(3, opts.MaxMergeChainDepth);
        Assert.AreEqual(20, opts.MaxRejectedMergesPerWeek);
        Assert.AreEqual(0, opts.MaxHardDeletesOutsidePurge);
        Assert.AreEqual(25, opts.MaxLossPercentBetweenSnapshots);
        Assert.AreEqual(5, opts.RepeatedRejectionRuns);
        Assert.AreEqual(0.45, opts.NearDuplicateThreshold);
    }

    [TestMethod]
    public void BindsTheModelTierByName()
    {
        var opts = Bind(new Dictionary<string, string?> { ["MemoryAudit:EvalModelTier"] = "Low" });

        Assert.AreEqual(ModelTier.Low, opts.EvalModelTier);
    }

    [TestMethod]
    public void MissingSectionLeavesTheCodeDefaults()
    {
        var opts = Bind(new Dictionary<string, string?> { ["Other:Key"] = "x" });

        Assert.IsTrue(opts.Enabled);
        Assert.AreEqual("0 4 * * *", opts.CronSchedule);
        Assert.AreEqual(MemoryAuditFiles.DefaultBasePath, opts.BasePath);
        Assert.AreEqual(TimeSpan.FromDays(400), opts.SnapshotRetention);
        Assert.AreEqual(0, opts.MaxHardDeletesOutsidePurge);
        Assert.IsFalse(opts.PauseConsolidationOnAlert, "The circuit breaker is opt-in.");
        Assert.IsTrue(opts.AlertOnAttention);
        Assert.IsNull(opts.DigestCronSchedule);
    }

    [TestMethod]
    public void AnExplicitFalseSurvivesTheBind()
    {
        // `dig` in the ConfigMap exists precisely so this reaches the options rather than
        // being swallowed by a default.
        var opts = Bind(new Dictionary<string, string?>
        {
            ["MemoryAudit:Enabled"] = "false",
            ["MemoryAudit:EvalEnabled"] = "false",
            ["MemoryAudit:CopyReportToShared"] = "false",
            ["MemoryAudit:AlertOnAttention"] = "false",
        });

        Assert.IsFalse(opts.Enabled);
        Assert.IsFalse(opts.EvalEnabled);
        Assert.IsFalse(opts.CopyReportToShared);
        Assert.IsFalse(opts.AlertOnAttention);
    }
}
