using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent;
using RockBot.Host;
using RockBot.Memory;
using RockBot.Messaging;
using RockBot.UserProxy;
// StubSessionTracker lives with the A2A test helpers; StubConversationMemory and
// TrackingPublisher resolve from this namespace, which takes precedence over the import.
using RockBot.Agent.A2A.Tests;

namespace RockBot.Agent.Tests;

[TestClass]
public class ClearContextHandlerTests
{
    private const string SessionId = "test-session";
    private const string AgentName = "TestBot";

    [TestMethod]
    public async Task HandleAsync_ClearsInjectedMemoryTracker()
    {
        var tracker = new InjectedMemoryTracker();
        var handler = BuildHandler(tracker);

        // A memory injected before the clear would otherwise stay suppressed for the life of
        // the process, leaving the fresh session unable to recall anything it had already seen.
        tracker.TryMarkAsInjected(SessionId, "mem-abc");
        Assert.IsFalse(tracker.TryMarkAsInjected(SessionId, "mem-abc"),
            "Precondition: the entry is suppressed before the context is cleared");

        await handler.HandleAsync(
            new ClearContextRequest { SessionId = SessionId }, BuildContext());

        Assert.IsTrue(tracker.TryMarkAsInjected(SessionId, "mem-abc"),
            "After clearing context the entry must be injectable again");
    }

    [TestMethod]
    public async Task HandleAsync_OnlyClearsTheTargetSession()
    {
        var tracker = new InjectedMemoryTracker();
        var handler = BuildHandler(tracker);

        tracker.TryMarkAsInjected(SessionId, "mem-abc");
        tracker.TryMarkAsInjected("other-session", "mem-abc");

        await handler.HandleAsync(
            new ClearContextRequest { SessionId = SessionId }, BuildContext());

        Assert.IsTrue(tracker.TryMarkAsInjected(SessionId, "mem-abc"));
        Assert.IsFalse(tracker.TryMarkAsInjected("other-session", "mem-abc"),
            "An unrelated session's injection state must survive");
    }

    private static ClearContextHandler BuildHandler(InjectedMemoryTracker tracker) =>
        new(
            new StubConversationMemory(),
            new StubSessionTracker(),
            new SessionClientCapabilityStore(),
            new ReplyAttachmentBuffer(),
            new SessionOriginStore(),
            tracker,
            new TrackingPublisher(),
            new AgentIdentity(AgentName),
            NullLogger<ClearContextHandler>.Instance);

    private static MessageHandlerContext BuildContext() => new()
    {
        Envelope = new ClearContextRequest { SessionId = SessionId }
            .ToEnvelope<ClearContextRequest>(source: "UserProxy"),
        Agent = new AgentIdentity(AgentName),
        Services = null!,
        CancellationToken = CancellationToken.None
    };
}
