using RockBot.Agent.McpBridge.Watches;

namespace RockBot.Agent.Tests.McpBridge;

/// <summary>
/// <see cref="McpRecentCalls"/>: a call is the cause of a change on the server it was made on, and
/// on the server an aggregator routed it to (#856), never on any other.
/// </summary>
[TestClass]
public class McpRecentCallsTests
{
    private const string Session = "session/test";

    [TestMethod]
    public void ARoutedCall_IsTheCauseOnTheAggregatorAndOnTheServerItReached()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var calls = new McpRecentCalls(clock);

        var call = calls.Start(Session, "aggregator", "set_event", routedServer: "calendar");
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.AreSame(call, calls.CauseOf(Session, "calendar", clock.GetUtcNow(), TimeSpan.Zero));
        Assert.AreSame(call, calls.CauseOf(Session, "Aggregator", clock.GetUtcNow(), TimeSpan.Zero));
        Assert.IsNull(calls.CauseOf(Session, "mail", clock.GetUtcNow(), TimeSpan.Zero));
        Assert.IsNull(calls.CauseOf("session/other", "calendar", clock.GetUtcNow(), TimeSpan.Zero));
    }

    [TestMethod]
    public void ADirectCall_IsNoCauseOnAnotherServer()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var calls = new McpRecentCalls(clock);

        var call = calls.Start(Session, "aggregator", "invoke_tool");
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.AreSame(call, calls.CauseOf(Session, "aggregator", clock.GetUtcNow(), TimeSpan.Zero));
        Assert.IsNull(calls.CauseOf(Session, "calendar", clock.GetUtcNow(), TimeSpan.Zero));
    }
}
