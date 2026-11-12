using static RockBot.UserProxy.ChatState.Tests.ChatEventFixtures;

namespace RockBot.UserProxy.ChatState.Tests;

[TestClass]
public class ChatStateServiceTests
{
    private sealed class ManualClock(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    [TestMethod]
    public void Apply_StampsUnstampedEventsWithTheClock()
    {
        var clock = new ManualClock(At(42));
        var service = new ChatStateService(clock);

        service.Apply(new UserMessageSent("hi", "u1", Session));

        Assert.AreEqual(At(42), service.State.Messages.Single().Timestamp);
    }

    [TestMethod]
    public void Apply_KeepsAnEventsOwnTimestamp()
    {
        var service = new ChatStateService(new ManualClock(At(42)));

        service.Apply(new UserMessageSent("hi", "u1", Session) { Timestamp = At(7) });

        Assert.AreEqual(At(7), service.State.Messages.Single().Timestamp);
    }

    [TestMethod]
    public void Apply_NotifiesOnlyWhenTheStateChanges()
    {
        var service = new ChatStateService(new ManualClock(At(0)));
        var notifications = 0;
        service.OnStateChanged += () => notifications++;

        Assert.IsTrue(service.Apply(new ErrorReported("boom")));
        Assert.AreEqual(1, notifications);

        // Toggling a message that doesn't exist changes nothing.
        Assert.IsFalse(service.Apply(new MessageExpandToggled("missing")));
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public void Apply_ReportsARefusedAnswer()
    {
        var service = new ChatStateService(new ManualClock(At(0)));
        service.Apply(Question("q1", atSeconds: 0));

        Assert.IsTrue(service.Apply(new McpAnswerSubmitted("q1")));
        Assert.IsFalse(service.Apply(new McpAnswerSubmitted("q1")), "already being sent");
    }

    [TestMethod]
    public void IsBusy_FollowsTheClock()
    {
        var clock = new ManualClock(At(0));
        var service = new ChatStateService(clock);
        service.Apply(Question("q1", atSeconds: 0, expiresInSeconds: 3600));
        service.Apply(new McpAnswerSubmitted("q1"));

        Assert.IsTrue(service.IsBusy);

        clock.UtcNow = At(0) + ChatViewState.FormWaitLimit;
        Assert.IsFalse(service.IsBusy);
    }

    [TestMethod]
    public async Task Apply_FromManyThreads_LosesNoEvents()
    {
        var service = new ChatStateService();

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            Task.Run(() => service.Apply(new ErrorReported($"e{i}")))));

        Assert.AreEqual(200, service.State.Messages.Count);
    }
}
