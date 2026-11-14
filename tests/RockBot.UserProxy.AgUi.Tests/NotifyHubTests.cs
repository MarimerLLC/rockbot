using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The notify stream's sequence, buffer and replay, without HTTP or a bus: ordering, the bounds
/// by count and by age, what a reconnect gets, when it is told to resync, and what happens to a
/// subscriber that doesn't keep up.
/// </summary>
[TestClass]
public sealed class NotifyHubTests
{
    private const string Thread = AgUiOptions.DefaultThreadId;

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();

    private NotifyHub Hub(int bufferSize = 200, TimeSpan? age = null, int queue = 256, string? epoch = "e1", params string[] threads)
    {
        var options = new AgUiOptions
        {
            NotifyBufferSize = bufferSize,
            NotifyBufferAge = age ?? TimeSpan.FromHours(24),
            NotifySubscriberQueueSize = queue,
        };
        options.AllowedThreadIds.AddRange(threads);
        return new NotifyHub(options, _clock, epoch: epoch);
    }

    /// <summary>Publishes one <c>rockbot.error</c> event carrying <paramref name="text"/> to the thread.</summary>
    private static void Publish(NotifyHub hub, string text, string thread = Thread) =>
        hub.PublishToThread(thread, m => [NotifyEvents.Error(m, text)]);

    private static List<NotifyFrame> Drain(NotifySubscription subscription)
    {
        var frames = new List<NotifyFrame>();
        while (subscription.Reader.TryRead(out var frame))
            frames.Add(frame);
        return frames;
    }

    private static JsonElement Json(NotifyFrame frame) => JsonDocument.Parse(frame.Json).RootElement;

    private static string Name(NotifyFrame frame) => Json(frame).GetProperty("name").GetString()!;

    /// <summary>The text <see cref="Publish"/> put in the event.</summary>
    private static string Text(NotifyFrame frame) => Json(frame).GetProperty("value").GetProperty("message").GetString()!;

    private static string[] Texts(IEnumerable<NotifyFrame> frames) => [.. frames.Select(Text)];

    // ── Ordering and ids ─────────────────────────────────────────────────────

    [TestMethod]
    public void Events_AreNumberedFromOne_InTheOrderTheyWerePublished()
    {
        var hub = Hub();
        using var subscription = hub.Subscribe(Thread)!;
        Drain(subscription);

        Publish(hub, "a");
        Publish(hub, "b");
        hub.PublishToThread(Thread, m => [NotifyEvents.Error(m, "c"), NotifyEvents.Error(m, "d")]);

        var frames = Drain(subscription);
        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d" }, Texts(frames));
        CollectionAssert.AreEqual(new[] { "e1-1", "e1-2", "e1-3", "e1-4" }, frames.Select(f => f.Id).ToArray());
    }

    [TestMethod]
    public void EachThread_HasItsOwnSequence()
    {
        var hub = Hub(threads: ["phone", "laptop"]);

        Publish(hub, "p1", "phone");
        Publish(hub, "p2", "phone");
        Publish(hub, "l1", "laptop");

        using var laptop = hub.Subscribe("laptop", "e1-0")!;
        var frames = Drain(laptop);
        Assert.AreEqual("e1-1", frames.Single().Id);
        Assert.AreEqual("l1", Text(frames[0]));
    }

    [TestMethod]
    public void EveryEvent_IsOneTheSdkCanReadBack()
    {
        var hub = Hub();
        Publish(hub, "a");

        using var subscription = hub.Subscribe(Thread, "e1-0")!;
        var parsed = JsonSerializer.Deserialize(Drain(subscription).Single().Json, AGUIJsonSerializerContext.Default.BaseEvent);

        Assert.IsInstanceOfType<CustomEvent>(parsed);
    }

    [TestMethod]
    public void Subscribe_ToAThreadThatIsNotAllowed_IsNull()
    {
        Assert.IsNull(Hub().Subscribe("blazor-session"));
    }

    // ── A new connection ─────────────────────────────────────────────────────

    [TestMethod]
    public void ANewSubscriber_GetsConnected_ThenOnlyWhatIsPublishedAfterwards()
    {
        var hub = Hub();
        Publish(hub, "before");

        using var subscription = hub.Subscribe(Thread)!;
        Publish(hub, "after");

        var frames = Drain(subscription);
        Assert.AreEqual(2, frames.Count);
        Assert.AreEqual(TurnEventMapper.CustomNames.Connected, Name(frames[0]));
        Assert.AreEqual(Thread, Json(frames[0]).GetProperty("value").GetProperty("threadId").GetString());
        Assert.AreEqual("after", Text(frames[1]));
    }

    [TestMethod]
    public void TheConnectedEvent_CarriesTheLastId_AndTakesNoNumberOfItsOwn()
    {
        var hub = Hub();
        Publish(hub, "a");

        using var first = hub.Subscribe(Thread)!;
        using var second = hub.Subscribe(Thread)!;
        Publish(hub, "b");

        Assert.AreEqual("e1-1", Drain(first)[0].Id, "the id to reconnect with: everything after it is new");
        var frames = Drain(second);
        Assert.AreEqual("e1-1", frames[0].Id);
        Assert.AreEqual("e1-2", frames[1].Id, "markers don't use up sequence numbers");
    }

    [TestMethod]
    public void ReconnectingWithTheConnectedEventsId_ReplaysEverythingPublishedSince()
    {
        var hub = Hub();
        string connectedId;
        using (var subscription = hub.Subscribe(Thread)!)
            connectedId = Drain(subscription).Single().Id;
        Assert.AreEqual("e1-0", connectedId);

        Publish(hub, "missed 1");
        Publish(hub, "missed 2");

        using var again = hub.Subscribe(Thread, connectedId)!;
        CollectionAssert.AreEqual(new[] { "missed 1", "missed 2" }, Texts(Drain(again)));
    }

    // ── Replay ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void TheBuffer_FillsWithNobodyConnected()
    {
        var hub = Hub();
        Publish(hub, "a");
        Publish(hub, "b");
        Publish(hub, "c");

        using var subscription = hub.Subscribe(Thread, "e1-1")!;

        var frames = Drain(subscription);
        CollectionAssert.AreEqual(new[] { "b", "c" }, Texts(frames));
        CollectionAssert.AreEqual(new[] { "e1-2", "e1-3" }, frames.Select(f => f.Id).ToArray());
    }

    [TestMethod]
    public void Replay_ThenLiveEvents_HaveNoGapAndNoDuplicate()
    {
        var hub = Hub();
        for (var i = 1; i <= 5; i++)
            Publish(hub, $"e{i}");

        using var subscription = hub.Subscribe(Thread, "e1-3")!;
        Publish(hub, "e6");
        Publish(hub, "e7");

        CollectionAssert.AreEqual(new[] { "e4", "e5", "e6", "e7" }, Texts(Drain(subscription)));
    }

    [TestMethod]
    public void ReconnectingWithTheLatestId_ReplaysNothing_AndIsNotAResync()
    {
        var hub = Hub();
        Publish(hub, "a");

        using var subscription = hub.Subscribe(Thread, "e1-1")!;

        Assert.AreEqual(0, Drain(subscription).Count);
    }

    // ── Bounds ───────────────────────────────────────────────────────────────

    [TestMethod]
    public void TheBuffer_KeepsOnlyTheLatestEvents()
    {
        var hub = Hub(bufferSize: 3);
        for (var i = 1; i <= 10; i++)
            Publish(hub, $"e{i}");

        // The oldest id that still reaches the buffer: the one just before its first event.
        using var subscription = hub.Subscribe(Thread, "e1-7")!;

        CollectionAssert.AreEqual(new[] { "e8", "e9", "e10" }, Texts(Drain(subscription)));
    }

    [TestMethod]
    public void AnIdFromBeforeTheBuffer_IsToldToResync_AndThenGetsLiveEvents()
    {
        var hub = Hub(bufferSize: 3);
        for (var i = 1; i <= 10; i++)
            Publish(hub, $"e{i}");

        using var subscription = hub.Subscribe(Thread, "e1-6")!;
        Publish(hub, "e11");

        var frames = Drain(subscription);
        Assert.AreEqual(2, frames.Count, "no partial replay: the gap can't be filled, so the client refetches");
        Assert.AreEqual(TurnEventMapper.CustomNames.Resync, Name(frames[0]));
        Assert.AreEqual(NotifyHub.ResyncReasons.Expired, Json(frames[0]).GetProperty("value").GetProperty("reason").GetString());
        Assert.AreEqual("e1-10", frames[0].Id, "the id to carry on from");
        Assert.AreEqual("e11", Text(frames[1]));
    }

    [TestMethod]
    public void TheBuffer_DropsEventsOlderThanItsAge()
    {
        var hub = Hub(age: TimeSpan.FromHours(1));
        Publish(hub, "old 1");
        Publish(hub, "old 2");
        _clock.Now += TimeSpan.FromMinutes(45);
        Publish(hub, "recent");
        _clock.Now += TimeSpan.FromMinutes(30);

        // "old 2" is 75 minutes old and gone, so a client that last saw "old 1" has a gap...
        using (var gap = hub.Subscribe(Thread, "e1-1")!)
            Assert.AreEqual(TurnEventMapper.CustomNames.Resync, Name(Drain(gap).Single()));

        // ...and one that saw "old 2" does not.
        using var subscription = hub.Subscribe(Thread, "e1-2")!;
        CollectionAssert.AreEqual(new[] { "recent" }, Texts(Drain(subscription)));
    }

    [TestMethod]
    public void WhenEverythingAgedOut_OnlyAClientThatSawItAllIsSpared()
    {
        var hub = Hub(age: TimeSpan.FromHours(1));
        Publish(hub, "a");
        Publish(hub, "b");
        _clock.Now += TimeSpan.FromHours(2);

        using (var behind = hub.Subscribe(Thread, "e1-1")!)
            Assert.AreEqual(TurnEventMapper.CustomNames.Resync, Name(Drain(behind).Single()));

        using var upToDate = hub.Subscribe(Thread, "e1-2")!;
        Assert.AreEqual(0, Drain(upToDate).Count);
    }

    [TestMethod]
    public void ABufferOfZero_KeepsNothing_ButLiveEventsStillFlow()
    {
        var hub = Hub(bufferSize: 0);
        using var live = hub.Subscribe(Thread)!;
        Publish(hub, "a");
        Publish(hub, "b");

        Assert.AreEqual(3, Drain(live).Count);
        using var behind = hub.Subscribe(Thread, "e1-1")!;
        Assert.AreEqual(TurnEventMapper.CustomNames.Resync, Name(Drain(behind).Single()));
    }

    // ── Ids this hub didn't issue ────────────────────────────────────────────

    [TestMethod]
    [DataRow("e0-2", DisplayName = "another process's id, same number")]
    [DataRow("2", DisplayName = "no epoch")]
    [DataRow("e1-", DisplayName = "no number")]
    [DataRow("e1-two", DisplayName = "not a number")]
    [DataRow("e1--2", DisplayName = "negative")]
    [DataRow("e1-99", DisplayName = "later than anything published")]
    [DataRow("-", DisplayName = "a dash")]
    public void AnIdThisHubDidNotIssue_IsToldToResync(string lastEventId)
    {
        var hub = Hub();
        Publish(hub, "a");
        Publish(hub, "b");
        Publish(hub, "c");

        using var subscription = hub.Subscribe(Thread, lastEventId)!;

        var frame = Drain(subscription).Single();
        Assert.AreEqual(TurnEventMapper.CustomNames.Resync, Name(frame));
        Assert.AreEqual(NotifyHub.ResyncReasons.Unknown, Json(frame).GetProperty("value").GetProperty("reason").GetString());
        Assert.AreEqual("e1-3", frame.Id);
    }

    [TestMethod]
    public void TwoHubs_NeverIssueTheSameId()
    {
        var options = new AgUiOptions();
        var before = new NotifyHub(options);
        var after = new NotifyHub(options);
        Assert.AreNotEqual(before.Epoch, after.Epoch);

        Publish(before, "a");
        Publish(after, "something else");
        string idFromBefore;
        using (var subscription = before.Subscribe(Thread, $"{before.Epoch}-0")!)
            idFromBefore = Drain(subscription).Single().Id;

        // The restarted proxy has an event number 1 of its own; the old id must not pass for it.
        using var reconnect = after.Subscribe(Thread, idFromBefore)!;
        Assert.AreEqual(TurnEventMapper.CustomNames.Resync, Name(Drain(reconnect).Single()));
    }

    // ── Subscribers that don't keep up ───────────────────────────────────────

    [TestMethod]
    public void ASubscriberThatFallsBehind_IsDropped_AndTheOthersAreNot()
    {
        var hub = Hub(bufferSize: 100, queue: 150);
        using var stuck = hub.Subscribe(Thread, "e1-0")!;
        using var reading = hub.Subscribe(Thread, "e1-0")!;

        var seenByReader = new List<NotifyFrame>();
        for (var i = 1; i <= 200; i++)
        {
            Publish(hub, $"e{i}");
            seenByReader.AddRange(Drain(reading));
        }

        Assert.IsTrue(stuck.IsDropped || stuck.Dropped.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(reading.IsDropped);
        Assert.AreEqual(200, seenByReader.Count, "the reader missed nothing while the other was stuck");

        // What the stuck one had queued is still there to be written, and then its stream ends.
        var queued = Drain(stuck);
        Assert.AreEqual(150, queued.Count);
        Assert.IsTrue(stuck.Reader.Completion.IsCompleted);

        // It reconnects from the last event it got and catches up from the buffer.
        using var again = hub.Subscribe(Thread, queued[^1].Id)!;
        var replay = Drain(again);
        Assert.AreEqual("e151", Text(replay[0]));
        Assert.AreEqual("e200", Text(replay[^1]));
        Assert.AreEqual(50, replay.Count);
    }

    [TestMethod]
    public void AReconnect_IsHandedAWholeBuffer_EvenWhenTheQueueIsConfiguredSmaller()
    {
        var hub = Hub(bufferSize: 50, queue: 4);
        for (var i = 1; i <= 50; i++)
            Publish(hub, $"e{i}");

        using var subscription = hub.Subscribe(Thread, "e1-0")!;

        Assert.IsFalse(subscription.IsDropped);
        Assert.AreEqual(50, Drain(subscription).Count);
    }

    [TestMethod]
    public void ADisposedSubscription_GetsNothingMore()
    {
        var hub = Hub();
        var subscription = hub.Subscribe(Thread)!;
        subscription.Dispose();

        Publish(hub, "a");

        Assert.AreEqual(1, Drain(subscription).Count, "only the connected event from before");
    }

    [TestMethod]
    public async Task PublishersAndReconnectingSubscribers_Concurrently_NobodySeesAGapOrADuplicate()
    {
        const int total = 5000;
        var hub = Hub(bufferSize: total);

        var publisher = Task.Run(() =>
        {
            for (var i = 1; i <= total; i++)
                Publish(hub, $"e{i}");
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            // Reconnect over and over, each time from the last id seen, until everything arrived.
            long last = 0;
            var spins = 0;
            while (last < total)
            {
                using var subscription = hub.Subscribe(Thread, $"e1-{last}")!;
                for (var reads = 0; reads < 50 && last < total; reads++)
                {
                    if (!subscription.Reader.TryRead(out var frame))
                    {
                        Assert.IsTrue(++spins < 50_000_000, "stuck waiting for events");
                        Thread_Yield();
                        continue;
                    }
                    Assert.AreEqual(TurnEventMapper.CustomNames.Error, Name(frame), "never a resync: the buffer holds everything");
                    var sequence = long.Parse(frame.Id["e1-".Length..]);
                    Assert.AreEqual(last + 1, sequence, "events arrive in order, each exactly once");
                    Assert.AreEqual($"e{sequence}", Text(frame));
                    last = sequence;
                }
            }
        })).ToArray();

        await Task.WhenAll([publisher, .. readers]).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void Thread_Yield() => System.Threading.Thread.Yield();
}
