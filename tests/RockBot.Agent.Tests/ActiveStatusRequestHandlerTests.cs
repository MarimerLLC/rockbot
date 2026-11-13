using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Subagent;
using RockBot.Subagent.Worker;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests;

/// <summary>The active-work snapshot covers workers and the asking session (#795).</summary>
[TestClass]
public sealed class ActiveStatusRequestHandlerTests
{
    private static async Task<ActiveStatusResponse> SnapshotAsync(ActiveStatusRequest request, FakeSessionTracker tracker)
    {
        var publisher = new RecordingPublisher();
        var subagents = new FakeSubagentManager(new SubagentEntry
        {
            TaskId = "sub-1",
            SubagentSessionId = "subagent-sub-1",
            PrimarySessionId = "session/s1",
            Description = "research",
            StartedAt = DateTimeOffset.UtcNow,
            CancellationTokenSource = new CancellationTokenSource(),
            Task = Task.Delay(Timeout.Infinite),
            ParentToolCallId = "call-1",
        });
        var workers = new FakeWorkerManager(
            new ActiveWorkerInfo("wrk-1", "gather", "session/s1", DateTimeOffset.UtcNow, "call-2"));
        var handler = new ActiveStatusRequestHandler(subagents, tracker, publisher,
            NullLogger<ActiveStatusRequestHandler>.Instance, workers);
        var agent = new AgentIdentity("test-agent");

        await handler.HandleAsync(request, new MessageHandlerContext
        {
            Envelope = request.ToEnvelope<ActiveStatusRequest>(source: "proxy", correlationId: "c1", replyTo: "agent.status.response.proxy"),
            Agent = agent,
            Services = new ServiceCollection().BuildServiceProvider(),
            CancellationToken = CancellationToken.None,
        });

        return publisher.Published.Single().Envelope.GetPayload<ActiveStatusResponse>()!;
    }

    [TestMethod]
    public async Task Snapshot_IncludesSubagentsAndWorkers()
    {
        var response = await SnapshotAsync(new ActiveStatusRequest(), new FakeSessionTracker());

        Assert.AreEqual("sub-1", response.Subagents.Single().TaskId, "older proxies still get subagents");
        Assert.AreEqual(2, response.Work!.Count);
        var subagent = response.Work.Single(w => w.Kind == ReplySourceKind.Subagent);
        var worker = response.Work.Single(w => w.Kind == ReplySourceKind.Worker);
        Assert.AreEqual("sub-1", subagent.TaskId);
        Assert.AreEqual("s1", subagent.ThreadId);
        Assert.AreEqual("call-1", subagent.ParentToolCallId);
        Assert.AreEqual("wrk-1", worker.TaskId);
        Assert.AreEqual("gather", worker.Description);
        Assert.AreEqual("call-2", worker.ParentToolCallId);
    }

    [TestMethod]
    public async Task IsProcessing_UsesTheRequestsSession()
    {
        var tracker = new FakeSessionTracker { Active = "blazor-abc" };

        Assert.IsTrue((await SnapshotAsync(new ActiveStatusRequest { SessionId = "blazor-abc" }, tracker)).IsProcessing);
        Assert.IsFalse((await SnapshotAsync(new ActiveStatusRequest { SessionId = "other" }, tracker)).IsProcessing);
    }

    [TestMethod]
    public async Task IsProcessing_WithoutASession_FallsBackToTheBlazorSession()
    {
        var tracker = new FakeSessionTracker { Active = "blazor-session" };

        Assert.IsTrue((await SnapshotAsync(new ActiveStatusRequest(), tracker)).IsProcessing);
        Assert.AreEqual("blazor-session", tracker.LastAsked);
    }

    private sealed class FakeSessionTracker : ISessionTracker
    {
        public string? Active { get; init; }
        public string? LastAsked { get; private set; }

        public SessionHandle BeginSession(string sessionId, CancellationToken hostCt) => new(hostCt, 1);
        public void EndSession(string sessionId, long generation) { }

        public bool HasActiveUserLoop(string sessionId)
        {
            LastAsked = sessionId;
            return sessionId == Active;
        }
    }

    private sealed class FakeSubagentManager(params SubagentEntry[] active) : ISubagentManager
    {
        public Task<string> SpawnAsync(string description, string? context, int? timeoutMinutes,
            string primarySessionId, CancellationToken ct, string? batchId = null, bool consolidate = true,
            int? maxIterations = null, string? originatingUserRequest = null, IReadOnlyList<SubagentInput>? inputs = null) =>
            throw new NotSupportedException();

        public Task<bool> CancelAsync(string taskId) => Task.FromResult(false);
        public IReadOnlyList<SubagentEntry> ListActive() => active;
    }

    private sealed class FakeWorkerManager(params ActiveWorkerInfo[] active) : IWorkerManager
    {
        public Task<WorkerBatchResult> SpawnBatchAsync(IReadOnlyList<WorkerDefinition> definitions,
            string primarySessionId, CancellationToken ct) => throw new NotSupportedException();

        public IReadOnlyList<ActiveWorkerInfo> ListActive() => active;
    }

    private sealed class RecordingPublisher : IMessagePublisher
    {
        public List<(string Topic, MessageEnvelope Envelope)> Published { get; } = [];

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Published.Add((topic, envelope));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
