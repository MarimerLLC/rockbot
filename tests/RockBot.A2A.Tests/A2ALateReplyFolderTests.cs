using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class A2ALateReplyFolderTests
{
    private readonly InMemoryWorkingMemory _memory = new();
    private readonly TrackingPublisher _publisher = new();
    private readonly AgentIdentity _agent = new("primary-agent");
    private readonly A2AOptions _options = new();

    private A2ALateReplyFolder CreateFolder(ISubagentSessionResolver? resolver) =>
        new(_publisher, _memory, _agent, _options,
            NullLogger<A2ALateReplyFolder>.Instance, resolver);

    private static PendingA2ATask Pending(string originSession) => new()
    {
        TaskId = "a2a-task-1",
        TargetAgent = "AdvisorCouncil",
        Skill = "deliberate",
        PrimarySessionId = originSession,
        StartedAt = DateTimeOffset.UtcNow,
        Cts = new CancellationTokenSource()
    };

    private sealed class FakeResolver : ISubagentSessionResolver
    {
        public bool Subagent { get; init; } = true;
        public bool Active { get; init; }
        public string? Primary { get; init; } = "session/blazor-session";

        public bool IsSubagentSession(string sessionId) => Subagent;
        public bool IsActive(string sessionId) => Active;
        public string? ResolvePrimarySession(string sessionId) => Primary;
    }

    [TestMethod]
    public async Task TerminatedSubagent_WithUserPrimary_FoldsBack()
    {
        var folder = CreateFolder(new FakeResolver { Active = false, Primary = "session/blazor-session" });

        var folded = await folder.TryFoldBackAsync(
            Pending("subagent/abc123"), "a2a-task-1", NotificationKind.Result, "the result text", CancellationToken.None);

        Assert.IsTrue(folded);

        // Payload stashed under the primary's notifications namespace + index appended.
        Assert.IsTrue(_memory.Writes.Any(w =>
            w.Key == "session/blazor-session/notifications/a2a/abc123/result" && w.Value == "the result text"));
        Assert.IsTrue(_memory.Writes.Any(w => w.Key == "session/blazor-session/notifications/index"));

        // LateA2ANotificationMessage published to the per-agent late-notification topic.
        Assert.AreEqual(1, _publisher.Published.Count);
        var (topic, envelope) = _publisher.Published[0];
        Assert.AreEqual("agent.late-notification.primary-agent", topic);
        var msg = envelope.GetPayload<LateA2ANotificationMessage>();
        Assert.AreEqual("session/blazor-session", msg!.PrimarySessionId);
        Assert.AreEqual("abc123", msg.SubagentTaskId);
        Assert.AreEqual("AdvisorCouncil", msg.PeerAgent);
        Assert.AreEqual(NotificationKind.Result, msg.Kind);
        Assert.AreEqual("session/blazor-session/notifications/a2a/abc123/result", msg.WorkingMemoryKey);
    }

    [TestMethod]
    public async Task ActiveSubagent_DoesNotFold()
    {
        var folder = CreateFolder(new FakeResolver { Active = true });

        var folded = await folder.TryFoldBackAsync(
            Pending("subagent/abc123"), "a2a-task-1", NotificationKind.Result, "x", CancellationToken.None);

        Assert.IsFalse(folded);
        Assert.AreEqual(0, _publisher.Published.Count);
        Assert.AreEqual(0, _memory.Writes.Count);
    }

    [TestMethod]
    public async Task NoResolver_DoesNotFold()
    {
        var folder = CreateFolder(resolver: null);

        var folded = await folder.TryFoldBackAsync(
            Pending("subagent/abc123"), "a2a-task-1", NotificationKind.Error, "x", CancellationToken.None);

        Assert.IsFalse(folded);
        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task UnresolvablePrimary_DoesNotFold()
    {
        var folder = CreateFolder(new FakeResolver { Active = false, Primary = null });

        var folded = await folder.TryFoldBackAsync(
            Pending("subagent/abc123"), "a2a-task-1", NotificationKind.Result, "x", CancellationToken.None);

        Assert.IsFalse(folded);
        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task NonUserPrimary_DoesNotFold()
    {
        // A primary that resolves to another non-user session must not produce a user bubble.
        var folder = CreateFolder(new FakeResolver { Active = false, Primary = "wisp-xyz" });

        var folded = await folder.TryFoldBackAsync(
            Pending("subagent/abc123"), "a2a-task-1", NotificationKind.Result, "x", CancellationToken.None);

        Assert.IsFalse(folded);
        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task NonSubagentOrigin_DoesNotFold()
    {
        var folder = CreateFolder(new FakeResolver { Subagent = false });

        var folded = await folder.TryFoldBackAsync(
            Pending("wisp-xyz"), "a2a-task-1", NotificationKind.Result, "x", CancellationToken.None);

        Assert.IsFalse(folded);
        Assert.AreEqual(0, _publisher.Published.Count);
    }
}
