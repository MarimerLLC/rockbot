extern alias fixture;

using fixture::McpServer.ElicitationFixture.Watches;
using RockBot.Agent.McpBridge.Watches;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// A subagent's watches pass to its user conversation once it exits (#865), end to end on a real
/// bridge and listen stream (<see cref="WatchableResources"/>): that conversation then lists,
/// renews and stops them, and hears of their changes, warnings and endings as its own.
/// </summary>
[TestClass]
public class McpWatchHandOverEndToEndTests
{
    private const string Primary = "session/test";
    private const string Subagent = "subagent/task-1";

    private static readonly string Poll = WatchableResources.UriFor("poll");

    private static async Task<string> WatchAsync(BridgeHarness harness, string session)
    {
        var response = await harness.WatchAsync(Poll, session);
        Assert.IsNull(response.Error, response.Error);
        return response.WatchId!;
    }

    private static McpWatchEntry LedgerEntry(BridgeHarness harness, string watchId) =>
        harness.ReadWatchLedger().Single(e => e.WatchId == watchId);

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            await Task.Delay(20);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnExitedSubagentsWatch_PassesToItsConversation_WhichListsAndStopsIt()
    {
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, Primary);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents);
        var watchId = await WatchAsync(harness, Subagent);

        Assert.AreEqual(Primary, LedgerEntry(harness, watchId).PrimarySessionId, "recorded while the subagent runs");
        StringAssert.Contains((await harness.ListWatchesAsync(Primary)).Message, "watches no resources",
            "a running subagent's watch is its own");

        subagents.Finish(Subagent);

        var listing = (await harness.ListWatchesAsync(Primary)).Message!;
        StringAssert.Contains(listing, watchId);
        StringAssert.Contains(listing, "Set up by subagent task-1, which has exited");
        var entry = LedgerEntry(harness, watchId);
        Assert.AreEqual(Primary, entry.SessionId);
        Assert.AreEqual(Subagent, entry.HandedOverFrom);

        var stopped = await harness.UnwatchAsync(Poll, Primary);
        Assert.IsNull(stopped.Error, stopped.Error);
        StringAssert.Contains(stopped.Message, "Stopped watching");
        Assert.AreEqual(0, harness.Bridge.ActiveWatchEntries.Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ChangesAndTheEnding_ReachItsConversation_EvenOnceTheSubagentIsForgotten()
    {
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, Primary);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents);
        var watchId = await WatchAsync(harness, Subagent);

        // Past the subagent's short-lived tombstone: only the recorded primary is left.
        subagents.Forget(Subagent);

        store.Set("poll", "v2");
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.AreEqual(Primary, update.SessionId, "an ordinary watch turn in the conversation, not a fold-back");
        Assert.AreEqual(watchId, update.TaskId);
        Assert.AreEqual("v2", update.Content);
        Assert.IsTrue(update.NeedsReply);

        store.Delete("poll");
        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchEnded);
        Assert.AreEqual(Primary, ended.SessionId);
        Assert.AreEqual(watchId, ended.TaskId);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnEndingThatSkipsTheHandOver_StillReachesItsConversation()
    {
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, Primary);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents);
        var watchId = await WatchAsync(harness, Subagent);
        subagents.Forget(Subagent);

        // Turning the server's watches off ends them directly, with no delivery to hand this one over first.
        harness.EditPersistedEntry(BridgeHarness.ServerName, e => e.Subscriptions = null);
        await harness.ReloadConfigAsync();

        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchEnded);
        Assert.AreEqual(Primary, ended.SessionId);
        Assert.AreEqual(watchId, ended.TaskId);
        StringAssert.Contains(ended.Content, "reconfigured");
        var entry = LedgerEntry(harness, watchId);
        Assert.AreEqual(Primary, entry.SessionId);
        Assert.AreEqual(Subagent, entry.HandedOverFrom);
        Assert.AreEqual(McpWatchEntryStatus.Ended, entry.Status);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnExpiryWarningAfterTheSubagentExits_GoesToItsConversation_WhichKeepsTheSameWatch()
    {
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, Primary);
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents, timeProvider: clock,
            configureBridge: o =>
            {
                o.WatchTtlDays = 30;
                o.WatchMaxLifetimeDays = 3;
                o.WatchExpiryWarningHours = 24;
                o.WatchDebounceMs = 0;
            });
        var watchId = await WatchAsync(harness, Subagent);
        subagents.Finish(Subagent);

        clock.Advance(TimeSpan.FromHours(60));
        var warning = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchExpiring);
        Assert.AreEqual(Primary, warning.SessionId);
        Assert.AreEqual(watchId, warning.TaskId);

        // Its advice works from the conversation: asking again keeps this watch, not a second one.
        var again = await harness.WatchWithInstructionsAsync(Poll, Primary, onChange: null, instructionsAllowed: false);
        Assert.IsNull(again.Error, again.Error);
        Assert.AreEqual(watchId, again.WatchId);
        StringAssert.Contains(again.Message, "already watches");
        Assert.AreEqual(1, harness.Bridge.ActiveWatchEntries.Count);
        Assert.AreEqual(clock.GetUtcNow(), LedgerEntry(harness, watchId).LifetimeStartedAt!.Value);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WhenItsConversationAlreadyWatchesTheResource_TheSubagentsWatchEnds_AndEachChangeArrivesOnce()
    {
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, Primary);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents);
        var mine = await WatchAsync(harness, Primary);
        var theirs = await WatchAsync(harness, Subagent);
        subagents.Finish(Subagent);

        store.Set("poll", "v2");
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        await Task.Delay(500);

        var updates = harness.TaskResults.Where(r => r.Kind == McpTaskResult.KindResourceUpdated).ToList();
        Assert.AreEqual(1, updates.Count, string.Join(" | ", updates.Select(u => $"{u.SessionId}/{u.TaskId}")));
        Assert.AreEqual(Primary, update.SessionId);
        Assert.AreEqual(mine, update.TaskId);
        Assert.AreEqual(mine, harness.Bridge.ActiveWatchEntries.Single().WatchId);
        Assert.AreEqual(McpWatchEntryStatus.Ended, LedgerEntry(harness, theirs).Status);
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindWatchEnded), "nothing to tell: the conversation still watches it");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARestart_HandsTheSubagentsWatchOver_AsItResumes()
    {
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, Primary);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents);
        var watchId = await WatchAsync(harness, Subagent);

        // No subagent, nor its tombstone, survives a restart.
        subagents.Forget(Subagent);
        await harness.RestartBridgeAsync();

        var resumed = harness.Bridge.ActiveWatchEntries.Single();
        Assert.AreEqual(watchId, resumed.WatchId);
        Assert.AreEqual(Primary, resumed.SessionId);
        Assert.AreEqual(Primary, LedgerEntry(harness, watchId).SessionId);

        store.Set("poll", "v2");
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v2");
        Assert.AreEqual(Primary, update.SessionId);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AnExitedSubagentsWatchWithNoConversationToTakeIt_EndsWithoutNotice()
    {
        // Spawned from something that isn't a user conversation, so nothing can take its watch.
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, "patrol/inbox");
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents);
        var watchId = await WatchAsync(harness, Subagent);
        Assert.IsNull(LedgerEntry(harness, watchId).PrimarySessionId);

        subagents.Finish(Subagent);
        await harness.ListWatchesAsync(Primary);

        Assert.AreEqual(0, harness.Bridge.ActiveWatchEntries.Count);
        var entry = LedgerEntry(harness, watchId);
        Assert.AreEqual(McpWatchEntryStatus.Ended, entry.Status);
        Assert.AreEqual(Subagent, entry.SessionId);
        await WaitUntilAsync(() => store.OpenStreams == 0, "the stream to close");
        Assert.AreEqual(0, harness.TaskResults.Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARunningSubagent_KeepsItsWatch()
    {
        var subagents = new McpHandbackEscalationTests.FakeSubagentSessions().Running(Subagent, Primary);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await McpWatchesEndToEndTests.StartAsync(store, subagentSessions: subagents);
        var watchId = await WatchAsync(harness, Subagent);

        StringAssert.Contains((await harness.UnwatchAsync(Poll, Primary)).Error, "doesn't watch");
        store.Set("poll", "v2");
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.AreEqual(Subagent, update.SessionId);
        Assert.AreEqual(watchId, update.TaskId);
        Assert.IsNull(LedgerEntry(harness, watchId).HandedOverFrom);
    }
}
