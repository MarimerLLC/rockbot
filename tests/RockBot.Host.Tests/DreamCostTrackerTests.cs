using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class DreamCostTrackerTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"dream-cost-{Guid.NewGuid():N}.json");

    [TestMethod]
    public async Task LoadAsync_WithMissingFile_YieldsZeroedTracker()
    {
        var tracker = await DreamCostTracker.LoadAsync(TempPath(), NullLogger.Instance);

        var lifetime = tracker.Lifetime;
        Assert.AreEqual(0, lifetime.TotalCycles);
        Assert.AreEqual(0, lifetime.TotalPassRuns);
        Assert.AreEqual(0.0, lifetime.TotalCostUsd);
        Assert.IsNull(lifetime.LastCycleAt);
    }

    [TestMethod]
    public async Task FinishCycle_FoldsRecordedPasses_IntoLifetimeAndLastCycle()
    {
        var tracker = await DreamCostTracker.LoadAsync(TempPath(), NullLogger.Instance);

        tracker.RecordPass(inputTokens: 1000, outputTokens: 100, cachedInputTokens: 400, costUsd: 0.5);
        tracker.RecordPass(inputTokens: 2000, outputTokens: 200, cachedInputTokens: 0, costUsd: 1.0);
        var now = DateTimeOffset.UtcNow;
        tracker.FinishCycle(now);

        var lifetime = tracker.Lifetime;
        Assert.AreEqual(1, lifetime.TotalCycles);
        Assert.AreEqual(2, lifetime.TotalPassRuns);
        Assert.AreEqual(3000, lifetime.TotalInputTokens);
        Assert.AreEqual(300, lifetime.TotalOutputTokens);
        Assert.AreEqual(400, lifetime.TotalCachedInputTokens);
        Assert.AreEqual(1.5, lifetime.TotalCostUsd, 0.0001);
        Assert.AreEqual(now, lifetime.LastCycleAt);
        Assert.AreEqual(2, lifetime.LastCyclePassRuns);
        Assert.AreEqual(1.5, lifetime.LastCycleCostUsd, 0.0001);
    }

    [TestMethod]
    public async Task FinishCycle_CalledTwice_DoesNotDoubleCount_AndResetsInProgressAccumulator()
    {
        var tracker = await DreamCostTracker.LoadAsync(TempPath(), NullLogger.Instance);

        tracker.RecordPass(1000, 100, 0, 1.0);
        tracker.FinishCycle(DateTimeOffset.UtcNow);
        // Second cycle recorded nothing (e.g. every pass was gate-skipped) — still a valid cycle.
        tracker.FinishCycle(DateTimeOffset.UtcNow);

        var lifetime = tracker.Lifetime;
        Assert.AreEqual(2, lifetime.TotalCycles);
        Assert.AreEqual(1, lifetime.TotalPassRuns);
        Assert.AreEqual(1.0, lifetime.TotalCostUsd, 0.0001);
        Assert.AreEqual(0, lifetime.LastCyclePassRuns);
        Assert.AreEqual(0.0, lifetime.LastCycleCostUsd);
    }

    [TestMethod]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsLifetimeTotals()
    {
        var path = TempPath();
        try
        {
            var tracker = await DreamCostTracker.LoadAsync(path, NullLogger.Instance);
            tracker.RecordPass(5000, 500, 1200, 2.25);
            tracker.FinishCycle(DateTimeOffset.UtcNow);
            await tracker.SaveAsync();

            var reloaded = await DreamCostTracker.LoadAsync(path, NullLogger.Instance);

            Assert.AreEqual(1, reloaded.Lifetime.TotalCycles);
            Assert.AreEqual(5000, reloaded.Lifetime.TotalInputTokens);
            Assert.AreEqual(1200, reloaded.Lifetime.TotalCachedInputTokens);
            Assert.AreEqual(2.25, reloaded.Lifetime.TotalCostUsd, 0.0001);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task LoadAsync_WithCorruptFile_YieldsZeroedTracker_RatherThanThrowing()
    {
        var path = TempPath();
        try
        {
            await File.WriteAllTextAsync(path, "{ not valid json");

            var tracker = await DreamCostTracker.LoadAsync(path, NullLogger.Instance);

            Assert.AreEqual(0, tracker.Lifetime.TotalCycles);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
