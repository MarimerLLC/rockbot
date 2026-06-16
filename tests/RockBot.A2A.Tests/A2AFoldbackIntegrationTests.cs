using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

/// <summary>
/// End-to-end coverage of the #424 fold-back path through the real
/// <see cref="A2ATaskResultHandler"/> + <see cref="A2ALateReplyFolder"/>: a terminal A2A
/// result for a subagent-issued task, arriving after the subagent has exited, must be
/// folded back to the subagent's owning primary session (a published
/// <see cref="LateA2ANotificationMessage"/>) rather than silently dropped.
///
/// The subagent-resolution itself (tombstone) is unit-tested in
/// <c>SubagentManagerTests</c>; here it is represented by a fake resolver so the test
/// focuses on the handler → folder → publish integration. Heavy LLM dependencies stay
/// <c>null!</c> because the non-user-session branch returns before touching them.
/// </summary>
[TestClass]
public class A2AFoldbackIntegrationTests
{
    private const string TargetAgent = "AdvisorCouncil";
    private const string TaskId = "a2a-task-77";
    // A subagent-issued A2A carries the subagent's working-memory namespace as the session.
    private const string SubagentSession = "subagent/sub-1";
    private const string PrimarySession = "session/blazor-session";

    private readonly InMemoryWorkingMemory _memory = new();
    private readonly A2ATaskTracker _tracker = new();
    private readonly TrackingPublisher _publisher = new();
    private readonly A2AOptions _options = new();
    private readonly AgentIdentity _agent = new("primary-agent");
    private readonly AgentNameHolder _nameHolder = new();

    /// <summary>Fake resolver standing in for SubagentManager's tombstone lookup.</summary>
    private sealed class FakeResolver(bool active) : ISubagentSessionResolver
    {
        public bool IsSubagentSession(string sessionId) => sessionId.StartsWith("subagent/", StringComparison.Ordinal);
        public bool IsActive(string sessionId) => active;
        public string? ResolvePrimarySession(string sessionId) => PrimarySession;
    }

    private A2ATaskResultHandler CreateHandler(bool subagentActive)
    {
        var folder = new A2ALateReplyFolder(
            _publisher, _memory, _agent, _options,
            NullLogger<A2ALateReplyFolder>.Instance, new FakeResolver(subagentActive));

        return new A2ATaskResultHandler(
            agentLoopRunner: null!,
            agentContextBuilder: null!,
            llmClient: null!,
            publisher: _publisher,
            agent: _agent,
            workingMemory: _memory,
            memoryTools: null!,
            skillStore: null!,
            toolRegistry: null!,
            rulesTools: null!,
            toolGuideTools: null!,
            conversationMemory: null!,
            tracker: _tracker,
            modelBehavior: null!,
            agentNameHolder: _nameHolder,
            inputRequiredHandler: null!,
            a2aOptions: _options,
            clientCapabilityStore: new SessionClientCapabilityStore(),
            originStore: new SessionOriginStore(),
            lateReplyFolder: folder,
            logger: NullLogger<A2ATaskResultHandler>.Instance);
    }

    private void TrackPending() =>
        _tracker.Track(new PendingA2ATask
        {
            TaskId = TaskId,
            TargetAgent = TargetAgent,
            Skill = "deliberate",
            PrimarySessionId = SubagentSession,
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource()
        });

    private static AgentTaskResult Result() => new()
    {
        TaskId = TaskId,
        State = AgentTaskState.Completed,
        Message = new AgentMessage
        {
            Role = "agent",
            Parts = [new AgentMessagePart { Kind = "text", Text = "The council recommends option B." }]
        }
    };

    private MessageHandlerContext Context(MessageEnvelope envelope) => new()
    {
        Envelope = envelope,
        Agent = _agent,
        Services = null!,
        CancellationToken = default
    };

    [TestMethod]
    public async Task TerminatedSubagent_LateResult_FoldsBackToPrimary()
    {
        TrackPending();
        var result = Result();
        var envelope = TestEnvelopeHelper.CreateEnvelope(result, correlationId: TaskId);

        await CreateHandler(subagentActive: false).HandleAsync(result, Context(envelope));

        // A LateA2ANotificationMessage was published to the primary agent's late-notification topic.
        var published = _publisher.Published
            .Where(p => p.Topic == "agent.late-notification.primary-agent")
            .ToList();
        Assert.AreEqual(1, published.Count, "expected exactly one fold-back notification");

        var msg = published[0].Envelope.GetPayload<LateA2ANotificationMessage>();
        Assert.IsNotNull(msg);
        Assert.AreEqual(PrimarySession, msg!.PrimarySessionId);
        Assert.AreEqual("sub-1", msg.SubagentTaskId);
        Assert.AreEqual(TargetAgent, msg.PeerAgent);
        Assert.AreEqual(NotificationKind.Result, msg.Kind);

        // Payload stashed under the primary's notifications namespace, with index appended.
        Assert.IsTrue(_memory.Writes.Any(w =>
                w.Key == $"{PrimarySession}/notifications/a2a/sub-1/result"
                && w.Value == "The council recommends option B."),
            "expected the result payload under the primary's notifications namespace");
        Assert.IsTrue(_memory.Writes.Any(w => w.Key == $"{PrimarySession}/notifications/index"),
            "expected a notifications index entry");
    }

    [TestMethod]
    public async Task ActiveSubagent_LateResult_DoesNotFold()
    {
        // The subagent is still running: the existing awaiter path delivers the result, so
        // the handler must NOT fold back (no notification, no primary-namespace writes).
        TrackPending();
        var result = Result();
        var envelope = TestEnvelopeHelper.CreateEnvelope(result, correlationId: TaskId);

        await CreateHandler(subagentActive: true).HandleAsync(result, Context(envelope));

        Assert.IsFalse(_publisher.Published.Any(p => p.Topic == "agent.late-notification.primary-agent"),
            "must not fold back while the subagent is still active");
        Assert.IsFalse(_memory.Writes.Any(w => w.Key.Contains("/notifications/")),
            "must not write notifications while the subagent is still active");
    }
}
