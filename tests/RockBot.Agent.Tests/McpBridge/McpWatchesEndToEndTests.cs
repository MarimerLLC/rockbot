extern alias fixture;

using System.Text.Json;
using fixture::McpServer.ElicitationFixture.Watches;
using ModelContextProtocol.Server;
using RockBot.Agent.McpBridge;
using RockBot.Agent.McpBridge.Watches;
using RockBot.Tools;
using RockBot.Tools.Mcp;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// Resource watches (#768) end to end: a real bridge holding a real <c>subscriptions/listen</c>
/// stream to an in-process MCP server whose listen handler is the live-test fixture's
/// (<see cref="WatchableResources"/>).
/// </summary>
[TestClass]
public class McpWatchesEndToEndTests
{
    private const string Session = "session/test";
    private const string Other = "session/other";

    private static readonly string Poll = WatchableResources.UriFor("poll");

    private static McpServerTool EchoTool() => McpServerTool.Create(
        (string text) => $"echo:{text}",
        new McpServerToolCreateOptions { Name = "echo" });

    internal static Task<BridgeHarness> StartAsync(
        WatchableResources store,
        bool policy = true,
        bool subscribable = true,
        Action<McpBridgeOptions>? configureBridge = null,
        TimeProvider? timeProvider = null) =>
        BridgeHarness.StartAsync(
            [EchoTool(), .. store.Tools()],
            resources: store.Resources(),
            configure: e => e.Subscriptions = policy ? true : null,
            configureBridge: o =>
            {
                o.WatchDebounceMs = 50;
                o.SubscriptionReconnectMinMs = 10;
                o.SubscriptionReconnectMaxMs = 200;
                o.SubscriptionAckTimeoutSeconds = 10;
                configureBridge?.Invoke(o);
            },
            timeProvider: timeProvider,
            configureServer: subscribable ? store.Configure : null);

    private static IReadOnlyList<McpTaskResult> Updates(BridgeHarness harness) =>
        [.. harness.TaskResults.Where(r => r.Kind == McpTaskResult.KindResourceUpdated)];

    private static async Task<string> WatchAsync(BridgeHarness harness, string uri = "", string session = Session)
    {
        var response = await harness.WatchAsync(uri.Length == 0 ? Poll : uri, session);
        Assert.IsNull(response.Error, response.Error);
        Assert.IsNotNull(response.WatchId);
        StringAssert.StartsWith(response.WatchId, "w_");
        return response.WatchId;
    }

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

    private static McpWatchEntry[] Ledger(BridgeHarness harness)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(harness.WatchLedgerPath));
        return doc.RootElement.GetProperty("entries").Deserialize<McpWatchEntry[]>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AChange_ReachesTheWatchingSession_WithItsContent()
    {
        var store = new WatchableResources();
        store.Set("poll", "state: open");
        await using var harness = await StartAsync(store);

        var watchId = await WatchAsync(harness);
        Assert.AreEqual(1, store.OpenStreams, "one stream carries the server's watches");
        CollectionAssert.AreEqual(new[] { Poll }, store.LastRequested.ToArray());
        Assert.AreEqual(0, Updates(harness).Count, "the content when the watch began is the baseline, not a change");

        store.Set("poll", "state: everyone voted");
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);

        Assert.AreEqual(Session, update.SessionId);
        Assert.AreEqual(watchId, update.TaskId);
        Assert.AreEqual(Poll, update.ResourceUri);
        Assert.AreEqual(BridgeHarness.ServerName, update.ServerName);
        Assert.AreEqual("state: everyone voted", update.Content);
        Assert.IsFalse(update.IsError);
        Assert.IsTrue(update.NeedsReply);

        // The ledger keeps the hash of what was delivered, never the content.
        await WaitUntilAsync(() => Ledger(harness).Single().LastHash == update.ContentHash, "the ledger to record the delivery");
        Assert.IsFalse(File.ReadAllText(harness.WatchLedgerPath).Contains("everyone voted"), "content is never written");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ABurstOfChanges_BecomesOneTurn_AndANoOpChangeNone()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store, configureBridge: o => o.WatchDebounceMs = 400);
        await WatchAsync(harness);

        store.Set("poll", "v2");
        store.Set("poll", "v3");
        store.Set("poll", "v4");
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.AreEqual("v4", update.Content, "one read, after the burst");

        await Task.Delay(800);
        Assert.AreEqual(1, Updates(harness).Count);

        // Notified, but the content is what the session already has.
        store.Set("poll", "v4");
        await Task.Delay(800);
        Assert.AreEqual(1, Updates(harness).Count, "an unchanged hash delivers nothing");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AChangeMissedWhileTheStreamWasDown_IsDeliveredWhenItReopens()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await WatchAsync(harness);

        // Settle first: once a pushed change has been delivered, the first opening's own catch-up
        // read is behind us, so only a reopen can find the next one.
        store.Set("poll", "v1b");
        await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v1b");
        var opened = store.StreamsOpened;

        // The server doesn't push this one, then ends the stream, as a restart or its lifetime would.
        store.SuppressNotifications = true;
        store.Set("poll", "v2");
        store.SuppressNotifications = false;
        store.EndStreams();

        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content != "v1b");
        Assert.AreEqual("v2", update.Content, "the re-read after reopening finds it");
        Assert.IsTrue(store.StreamsOpened > opened, "the bridge reopened the stream");

        // And the reopened stream carries the next change as usual.
        store.Set("poll", "v3");
        await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v3");

        // Closing and reopening a stream is routine, never a failure (it once threw on every close).
        Assert.IsFalse(harness.BridgeLog.Any(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error),
            string.Join(" | ", harness.BridgeLog.Where(e => e.Level >= Microsoft.Extensions.Logging.LogLevel.Error).Select(e => e.Message)));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ARestart_ResumesTheWatchFromTheLedger_AndDeliversWhatChangedMeanwhile()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        var watchId = await WatchAsync(harness);

        store.SuppressNotifications = true;
        store.Set("poll", "v2 (changed while the agent was down)");
        await harness.RestartBridgeAsync();
        store.SuppressNotifications = false;

        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.AreEqual(watchId, update.TaskId, "the same watch, resumed");
        Assert.AreEqual("v2 (changed while the agent was down)", update.Content);

        store.Set("poll", "v3");
        await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated && r.Content == "v3");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Unwatch_StopsUpdates_AndClosesTheStream()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await WatchAsync(harness);

        var stopped = await harness.UnwatchAsync(Poll, Session);
        Assert.IsNull(stopped.Error, stopped.Error);
        StringAssert.Contains(stopped.Message, "Stopped watching");

        await WaitUntilAsync(() => store.OpenStreams == 0, "the stream to close");
        store.Set("poll", "v2");
        await Task.Delay(500);
        Assert.AreEqual(0, harness.TaskResults.Count, "nothing is delivered, not even an ending the agent asked for");
        Assert.AreEqual(McpWatchEntryStatus.Ended, Ledger(harness).Single().Status);

        var again = await harness.UnwatchAsync(Poll, Session);
        StringAssert.Contains(again.Error, "doesn't watch");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWatchOfAnotherSession_IsKeptWhenOneSessionUnwatches()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await WatchAsync(harness);
        var other = await WatchAsync(harness, session: Other);

        await harness.UnwatchAsync(Poll, Session);
        store.Set("poll", "v2");

        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.AreEqual(Other, update.SessionId);
        Assert.AreEqual(other, update.TaskId);
        Assert.AreEqual(1, store.OpenStreams);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AResourceThatIsDeleted_EndsTheWatch_AndTellsTheSession()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        var watchId = await WatchAsync(harness);

        store.Delete("poll");
        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchEnded);
        Assert.AreEqual(watchId, ended.TaskId);
        StringAssert.Contains(ended.Content, "no longer exists");
        Assert.IsTrue(ended.NeedsReply);
        await WaitUntilAsync(() => harness.Bridge.ActiveWatchEntries.Count == 0, "the watch to end");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WatchingTheSameResourceTwice_KeepsOneWatch()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        var first = await WatchAsync(harness);

        var second = await harness.WatchAsync(Poll, Session);
        Assert.IsNull(second.Error);
        Assert.AreEqual(first, second.WatchId);
        StringAssert.Contains(second.Message, "already watches");
        Assert.AreEqual(1, harness.Bridge.ActiveWatchEntries.Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AResourceTheServerDoesntAcknowledge_IsRefused()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        store.Unacknowledged[Poll] = 0;
        await using var harness = await StartAsync(store);

        var response = await harness.WatchAsync(Poll, Session);
        StringAssert.Contains(response.Error, "didn't acknowledge");
        Assert.AreEqual(0, harness.Bridge.ActiveWatchEntries.Count);
        Assert.AreEqual(0, harness.TaskResults.Count, "a refused watch isn't announced as ended");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AResourceThatCantBeRead_IsRefused()
    {
        var store = new WatchableResources();
        await using var harness = await StartAsync(store);

        var response = await harness.WatchAsync(WatchableResources.UriFor("missing"), Session);
        StringAssert.Contains(response.Error, "reading it failed");
        Assert.AreEqual(0, harness.Bridge.ActiveWatchEntries.Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TheCaps_RefuseWithTheSessionsWatches()
    {
        var store = new WatchableResources();
        store.Set("a", "1");
        store.Set("b", "2");
        await using var harness = await StartAsync(store, configureBridge: o =>
        {
            o.MaxWatchesPerSession = 1;
            o.MaxWatches = 2;
        });

        var first = await WatchAsync(harness, WatchableResources.UriFor("a"));
        var perSession = await harness.WatchAsync(WatchableResources.UriFor("b"), Session);
        StringAssert.Contains(perSession.Error, "this conversation already watches 1");
        StringAssert.Contains(perSession.Error, first, "the error lists the session's watches");

        await WatchAsync(harness, WatchableResources.UriFor("b"), session: Other);
        var total = await harness.WatchAsync(WatchableResources.UriFor("a"), "session/third");
        StringAssert.Contains(total.Error, "the agent already watches 2");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task OnlySessionsThatOutliveTheCall_CanWatch()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);

        foreach (var session in new[] { "scheduled/daily-brief", "patrol/inbox", "session/subagent-1", "worker/abc" })
        {
            var response = await harness.WatchAsync(Poll, session);
            StringAssert.Contains(response.Error, "Only a user conversation or a subagent", session);
        }

        await WatchAsync(harness, session: "subagent/task-1");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AServerWithoutThePolicy_OrWithoutSubscriptions_CantBeWatched()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");

        await using (var noPolicy = await StartAsync(store, policy: false))
        {
            Assert.IsFalse(noPolicy.IndexMessages.Last().Servers.Single().Subscriptions);
            StringAssert.Contains((await noPolicy.WatchAsync(Poll, Session)).Error, "isn't set up for resource watches");
        }

        await using (var noCapability = await StartAsync(store, subscribable: false))
        {
            Assert.IsFalse(noCapability.IndexMessages.Last().Servers.Single().Subscriptions);
            StringAssert.Contains((await noCapability.WatchAsync(Poll, Session)).Error, "doesn't offer resource subscriptions");
        }

        await using (var both = await StartAsync(store))
            Assert.IsTrue(both.IndexMessages.Last().Servers.Single().Subscriptions);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AServerThatRefusesSubscriptionsListen_IsRefused()
    {
        var store = new WatchableResources { RefuseListen = true };
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);

        var response = await harness.WatchAsync(Poll, Session);
        Assert.IsNotNull(response.Error);
        StringAssert.Contains(response.Error, "can't be watched");
        await WaitUntilAsync(() => harness.Bridge.ActiveWatchEntries.Count == 0, "no watch to remain");
        Assert.AreEqual(0, harness.TaskResults.Count);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TurningThePolicyOff_EndsTheWatches_AndTellsTheSessions()
    {
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store);
        await WatchAsync(harness);

        harness.EditPersistedEntry(BridgeHarness.ServerName, e => e.Subscriptions = null);
        await harness.ReloadConfigAsync();

        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchEnded);
        StringAssert.Contains(ended.Content, "reconfigured");
        await WaitUntilAsync(() => store.OpenStreams == 0, "the stream to close");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWatch_IsRenewed_WhileItsResourceExists()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store, timeProvider: clock, configureBridge: o =>
        {
            o.WatchTtlDays = 2;
            o.WatchMaxLifetimeDays = 30;
            o.WatchDebounceMs = 0;
        });
        var response = await harness.WatchAsync(Poll, Session);
        Assert.IsNull(response.Error, response.Error);
        StringAssert.Contains(response.Message, "renews itself while the resource exists");
        StringAssert.Contains(response.Message, "after 30 day(s) at most");
        var firstExpiry = Ledger(harness).Single().ExpiresAt;

        // A poll whose deadline is weeks away outlives one renewal period (#778).
        clock.Advance(TimeSpan.FromDays(3));
        await WaitUntilAsync(() => Ledger(harness).Single().ExpiresAt > firstExpiry, "the renewal to be recorded");

        var entry = Ledger(harness).Single();
        Assert.AreEqual(McpWatchEntryStatus.Active, entry.Status);
        Assert.IsTrue(entry.ExpiresAt > clock.GetUtcNow(), "renewed into the future");
        Assert.AreEqual(1, harness.Bridge.ActiveWatchEntries.Count);
        Assert.IsFalse(harness.TaskResults.Any(r => r.Kind == McpTaskResult.KindWatchEnded), "a renewal tells the session nothing");

        store.Set("poll", "v2");
        var update = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindResourceUpdated);
        Assert.AreEqual(response.WatchId, update.TaskId, "the renewed watch still delivers");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWatch_EndsAtItsMaximumLifetime()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store, timeProvider: clock, configureBridge: o =>
        {
            o.WatchTtlDays = 2;
            o.WatchMaxLifetimeDays = 3;
            o.WatchDebounceMs = 0;
        });
        var watchId = await WatchAsync(harness);

        clock.Advance(TimeSpan.FromDays(4));
        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchEnded);
        Assert.AreEqual(watchId, ended.TaskId);
        StringAssert.Contains(ended.Content, "maximum lifetime of 3 day(s)");
        await WaitUntilAsync(() => Ledger(harness).Single().ResolvedAt is not null, "the ending to settle");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AWatchDueForRenewal_WhoseResourceIsGone_Ends()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new WatchableResources();
        store.Set("poll", "v1");
        await using var harness = await StartAsync(store, timeProvider: clock, configureBridge: o =>
        {
            o.WatchTtlDays = 2;
            o.WatchMaxLifetimeDays = 30;
            o.WatchDebounceMs = 0;
        });
        var watchId = await WatchAsync(harness);

        // Gone without a push, so only the renewal check finds out.
        store.SuppressNotifications = true;
        store.Delete("poll");

        clock.Advance(TimeSpan.FromDays(3));
        var ended = await harness.WaitForTaskResultAsync(r => r.Kind == McpTaskResult.KindWatchEnded);
        Assert.AreEqual(watchId, ended.TaskId);
        StringAssert.Contains(ended.Content, "no longer");
        await WaitUntilAsync(() => harness.Bridge.ActiveWatchEntries.Count == 0, "the watch to end");
    }

    [TestMethod]
    public void TheContentHash_IsStable_AndBinaryContentIsDescribedNotInlined()
    {
        var text = new ModelContextProtocol.Protocol.ReadResourceResult
        {
            Contents = [new ModelContextProtocol.Protocol.TextResourceContents { Uri = Poll, Text = "hello" }],
        };
        var same = new ModelContextProtocol.Protocol.ReadResourceResult
        {
            Contents = [new ModelContextProtocol.Protocol.TextResourceContents { Uri = Poll, Text = "hello" }],
        };
        var other = new ModelContextProtocol.Protocol.ReadResourceResult
        {
            Contents = [new ModelContextProtocol.Protocol.TextResourceContents { Uri = Poll, Text = "hello!" }],
        };
        Assert.AreEqual(McpBridgeService.HashContents(text), McpBridgeService.HashContents(same));
        Assert.AreNotEqual(McpBridgeService.HashContents(text), McpBridgeService.HashContents(other));

        var binary = new ModelContextProtocol.Protocol.ReadResourceResult
        {
            Contents = [ModelContextProtocol.Protocol.BlobResourceContents.FromBytes(new byte[2048], "fixture://watch/img", "image/png")],
        };
        var rendered = McpBridgeService.RenderContents(binary);
        StringAssert.Contains(rendered, "binary content: image/png, 2,048 bytes");
    }
}
