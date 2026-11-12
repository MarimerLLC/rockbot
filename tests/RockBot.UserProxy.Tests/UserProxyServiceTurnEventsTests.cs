using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

/// <summary>
/// Run lifecycle, task lists and broadcast events (#795) as the proxy sees them.
/// </summary>
[TestClass]
public sealed class UserProxyServiceTurnEventsTests
{
    private TrackingPublisher _publisher = null!;
    private StubSubscriber _subscriber = null!;
    private StubUserFrontend _frontend = null!;
    private UserProxyOptions _options = null!;
    private UserProxyService _service = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _publisher = new TrackingPublisher();
        _subscriber = new StubSubscriber();
        _frontend = new StubUserFrontend();
        _options = new UserProxyOptions
        {
            ProxyId = "test-proxy",
            RunFinishedGracePeriod = TimeSpan.FromMilliseconds(100),
        };
        _service = new UserProxyService(_publisher, _subscriber, _frontend, _options,
            NullLogger<UserProxyService>.Instance);
        await _service.StartAsync(CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup() => await _service.StopAsync(CancellationToken.None);

    private Func<MessageEnvelope, CancellationToken, Task<MessageResult>> P2P =>
        _subscriber.GetHandlerForTopic($"{UserProxyTopics.UserResponse}.{_options.AgentName}.{_options.ProxyId}")!;

    private Func<MessageEnvelope, CancellationToken, Task<MessageResult>> Events =>
        _subscriber.GetHandlerForTopic($"{UserProxyTopics.UserEvent}.{_options.AgentName}")!;

    private static UserMessage Message() => new() { Content = "hi", SessionId = "s1", UserId = "u1" };

    private async Task<string> PublishedCorrelationIdAsync()
    {
        for (var i = 0; i < 100 && _publisher.Published.Count == 0; i++)
            await Task.Delay(5);
        return _publisher.Published[0].Envelope.CorrelationId!;
    }

    private Task<MessageResult> SendToP2P<T>(T payload, string correlationId) =>
        P2P(TestEnvelopeHelper.CreateEnvelope(payload, source: "agent", correlationId: correlationId), CancellationToken.None);

    private static RunStarted Started(string id, int interval = 15) => new()
    {
        ThreadId = "s1", RunId = id, AgentName = "agent", HeartbeatIntervalSeconds = interval, Timestamp = DateTimeOffset.UtcNow,
    };

    private static RunHeartbeat Heartbeat(string id, string? status = null) => new()
    {
        ThreadId = "s1", RunId = id, Timestamp = DateTimeOffset.UtcNow, ElapsedSeconds = 1, Status = status,
    };

    private static RunFinished Finished(string id, string outcome, string? error = null) => new()
    {
        ThreadId = "s1", RunId = id, Outcome = outcome, Error = error, Timestamp = DateTimeOffset.UtcNow,
    };

    private static AgentReply Reply(string content = "done", bool isFinal = true) => new()
    {
        Content = content, SessionId = "s1", AgentName = "agent", IsFinal = isFinal,
    };

    [TestMethod]
    public void StartAsync_SubscribesToTheEventTopic()
    {
        Assert.IsNotNull(_subscriber.GetHandlerForTopic($"{UserProxyTopics.UserEvent}.{_options.AgentName}"));
    }

    [TestMethod]
    public async Task SendAsync_SetsTheAgentEventsCapability()
    {
        await _service.SendAsync(Message() with { ClientCapabilities = ClientCapabilityPresets.Cli },
            timeout: TimeSpan.FromMilliseconds(20));

        var sent = _publisher.Published[0].Envelope.GetPayload<UserMessage>()!;
        Assert.IsTrue(sent.ClientCapabilities.HasFlag(ClientCapabilities.AgentEvents));
        Assert.IsTrue(sent.ClientCapabilities.HasFlag(ClientCapabilities.MarkdownCode), "the frontend's own bits are kept");
    }

    [TestMethod]
    public async Task SendFireAndForgetAsync_SetsTheAgentEventsCapability()
    {
        await _service.SendFireAndForgetAsync(Message());

        var sent = _publisher.Published[0].Envelope.GetPayload<UserMessage>()!;
        Assert.IsTrue(sent.ClientCapabilities.HasFlag(ClientCapabilities.AgentEvents));
    }

    [TestMethod]
    public async Task SendTurnAsync_UsesTheCallersCorrelationId()
    {
        var task = _service.SendTurnAsync(Message(), new TurnOptions { CorrelationId = "turn-42" });
        var id = await PublishedCorrelationIdAsync();
        Assert.AreEqual("turn-42", id);

        await SendToP2P(Reply(), id);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(TurnResultOutcome.Success, result.Outcome);
        Assert.AreEqual("turn-42", result.CorrelationId);
    }

    [TestMethod]
    public async Task SendTurnAsync_OldAgentWithoutEvents_TimesOut()
    {
        var result = await _service.SendTurnAsync(Message(), new TurnOptions { Timeout = TimeSpan.FromMilliseconds(50) });

        Assert.AreEqual(TurnResultOutcome.TimedOut, result.Outcome);
        Assert.IsNull(result.Reply);
        Assert.IsFalse(result.RunStarted);
    }

    [TestMethod]
    public async Task SendTurnAsync_RunStarted_SwitchesToTheHeartbeatDeadline()
    {
        // The overall timeout is short; once the run has started, heartbeats keep the turn alive past it.
        var task = _service.SendTurnAsync(Message(), new TurnOptions
        {
            Timeout = TimeSpan.FromMilliseconds(150),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(300),
        });
        var id = await PublishedCorrelationIdAsync();

        await SendToP2P(Started(id), id);
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(100);
            await SendToP2P(Heartbeat(id), id);
        }
        Assert.IsFalse(task.IsCompleted, "heartbeats keep the turn alive past the overall timeout");

        await SendToP2P(Reply(), id);
        await SendToP2P(Finished(id, RunOutcome.Success), id);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(TurnResultOutcome.Success, result.Outcome);
        Assert.AreEqual("done", result.Reply?.Content);
        Assert.IsTrue(result.RunStarted);
    }

    [TestMethod]
    public async Task SendTurnAsync_MissedHeartbeat_IsAgentLost()
    {
        var task = _service.SendTurnAsync(Message(), new TurnOptions
        {
            Timeout = TimeSpan.FromSeconds(30),
            HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
        });
        var id = await PublishedCorrelationIdAsync();
        await SendToP2P(Started(id), id);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(TurnResultOutcome.AgentLost, result.Outcome);
        Assert.IsNull(result.Reply);
    }

    [TestMethod]
    public async Task SendTurnAsync_HeartbeatTimeoutDefaultsToThreeIntervals()
    {
        _options.HeartbeatTimeout = null;
        var task = _service.SendTurnAsync(Message(), new TurnOptions { Timeout = TimeSpan.FromMilliseconds(50) });
        var id = await PublishedCorrelationIdAsync();
        await SendToP2P(Started(id, interval: 15), id);

        await Task.Delay(200);
        Assert.IsFalse(task.IsCompleted, "45 seconds of silence are allowed, not the 50 ms overall timeout");

        await SendToP2P(Reply(), id);
        Assert.AreEqual(TurnResultOutcome.Success, (await task.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
    }

    [TestMethod]
    public async Task SendTurnAsync_RunCancelledWithoutReply_IsCancelled()
    {
        var task = _service.SendTurnAsync(Message(), new TurnOptions { Timeout = TimeSpan.FromSeconds(30) });
        var id = await PublishedCorrelationIdAsync();
        await SendToP2P(Started(id), id);
        await SendToP2P(Finished(id, RunOutcome.Cancelled), id);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(TurnResultOutcome.Cancelled, result.Outcome);
        Assert.IsNull(result.Reply);
    }

    [TestMethod]
    public async Task SendTurnAsync_RunErrorThenReply_IsErrorWithTheReply()
    {
        var task = _service.SendTurnAsync(Message(), new TurnOptions { Timeout = TimeSpan.FromSeconds(30) });
        var id = await PublishedCorrelationIdAsync();
        await SendToP2P(Started(id), id);
        await SendToP2P(Finished(id, RunOutcome.Error, "LLM unavailable"), id);
        await SendToP2P(Reply("Sorry, I ran into an error"), id);

        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(TurnResultOutcome.Error, result.Outcome);
        Assert.AreEqual("LLM unavailable", result.Error);
        Assert.AreEqual("Sorry, I ran into an error", result.Reply?.Content);
    }

    [TestMethod]
    public async Task SendTurnAsync_ReportsEventsAndProgressToTheTurn()
    {
        var observer = new RecordingObserver();
        var progress = new List<AgentReply>();
        var task = _service.SendTurnAsync(Message(), new TurnOptions
        {
            Observer = observer,
            Progress = new SyncProgress<AgentReply>(progress.Add),
            Timeout = TimeSpan.FromSeconds(30),
        });
        var id = await PublishedCorrelationIdAsync();

        await SendToP2P(Started(id), id);
        await SendToP2P(Heartbeat(id, "Calling web_search"), id);
        await SendToP2P(new TaskListSnapshot
        {
            ThreadId = "s1", RunId = id, Source = ReplySource.Primary,
            Items = [new TaskListItem { Id = "1", Description = "Search", Status = "pending" }],
        }, id);
        await SendToP2P(new TaskListDelta
        {
            ThreadId = "s1", RunId = id, Source = ReplySource.Primary,
            Item = new TaskListItem { Id = "1", Description = "Search", Status = "completed" },
        }, id);
        await SendToP2P(Reply("Calling web_search…", isFinal: false) with
        {
            ToolCall = new ToolCallInfo { ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started },
        }, id);
        await SendToP2P(Reply(), id);
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, observer.Started.Count);
        Assert.AreEqual("Calling web_search", observer.Heartbeats.Single().Status);
        Assert.AreEqual(1, observer.Snapshots.Count);
        Assert.AreEqual("completed", observer.Deltas.Single().Item.Status);
        Assert.AreEqual("c1", progress.Single().ToolCall?.ToolCallId);
        Assert.AreEqual(0, _frontend.RunsStarted.Count, "the turn's own events don't reach the frontend");
        Assert.AreEqual(0, _frontend.TaskListSnapshots.Count);
    }

    [TestMethod]
    public async Task RunEventsAfterTheReply_AreDropped()
    {
        var task = _service.SendTurnAsync(Message(), new TurnOptions { Timeout = TimeSpan.FromSeconds(30) });
        var id = await PublishedCorrelationIdAsync();
        await SendToP2P(Started(id), id);
        await SendToP2P(Reply(), id);
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(MessageResult.Ack, await SendToP2P(Finished(id, RunOutcome.Success), id));
        await Task.Delay(20);
        Assert.AreEqual(0, _frontend.RunsFinished.Count);
        Assert.AreEqual(0, _frontend.DisplayedReplies.Count);
    }

    [TestMethod]
    public async Task LateRunEvents_ForATurnThatTimedOut_GoToTheFrontend()
    {
        var result = await _service.SendTurnAsync(Message(), new TurnOptions { Timeout = TimeSpan.FromMilliseconds(30) });
        Assert.AreEqual(TurnResultOutcome.TimedOut, result.Outcome);
        var id = result.CorrelationId;

        await SendToP2P(Started(id), id);
        await SendToP2P(Heartbeat(id), id);
        await SendToP2P(Finished(id, RunOutcome.Success), id);
        await Task.Delay(20);

        Assert.AreEqual(1, _frontend.RunsStarted.Count);
        Assert.AreEqual(1, _frontend.RunHeartbeats.Count);
        Assert.AreEqual(1, _frontend.RunsFinished.Count);
        Assert.AreEqual(0, _frontend.DisplayedReplies.Count, "run events are never mistaken for replies");
    }

    [TestMethod]
    public async Task UnknownRockBotUserProxyType_IsAcked()
    {
        var envelope = MessageEnvelope.Create(
            messageType: "RockBot.UserProxy.SomethingFromTheFuture",
            body: "{\"x\":1}"u8.ToArray(),
            source: "agent");

        Assert.AreEqual(MessageResult.Ack, await _subscriber.CapturedHandler!(envelope, CancellationToken.None));
        Assert.AreEqual(0, _frontend.DisplayedReplies.Count);
        Assert.AreEqual(0, _frontend.DisplayedStatusReplies.Count);
    }

    [TestMethod]
    public async Task EventTopic_DispatchesToTheFrontend()
    {
        var now = DateTimeOffset.UtcNow;
        await Events(TestEnvelopeHelper.CreateEnvelope(new SubagentStarted
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Worker, Description = "summarize", ThreadId = "s1", StartedAt = now,
        }), CancellationToken.None);
        await Events(TestEnvelopeHelper.CreateEnvelope(new SubagentFinished
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Worker, ThreadId = "s1", Outcome = SubagentOutcome.Success, FinishedAt = now,
        }), CancellationToken.None);
        await Events(TestEnvelopeHelper.CreateEnvelope(new ActiveStatusResponse
        {
            Subagents = [], IsProcessing = false, Work = [],
        }), CancellationToken.None);
        await Events(TestEnvelopeHelper.CreateEnvelope(new TaskListSnapshot
        {
            ThreadId = "s1", Source = ReplySource.Subagent("t2"), Items = [],
        }), CancellationToken.None);
        var unknown = await Events(MessageEnvelope.Create("RockBot.UserProxy.Unknown", "{}"u8.ToArray(), "agent"),
            CancellationToken.None);
        await Task.Delay(20);

        Assert.AreEqual(MessageResult.Ack, unknown);
        Assert.AreEqual(ReplySourceKind.Worker, _frontend.SubagentsStarted.Single().Kind);
        Assert.AreEqual(SubagentOutcome.Success, _frontend.SubagentsFinished.Single().Outcome);
        Assert.AreEqual(1, _frontend.WorkSnapshots.Count);
        Assert.AreEqual("t2", _frontend.TaskListSnapshots.Single().Source.TaskId);
    }

    [TestMethod]
    public async Task GetActiveStatusAsync_SendsTheSessionId()
    {
        await _service.GetActiveStatusAsync("blazor-abc", TimeSpan.FromMilliseconds(20));

        var request = _publisher.Published.Single(p => p.Topic.StartsWith(UserProxyTopics.ActiveStatusRequest))
            .Envelope.GetPayload<ActiveStatusRequest>()!;
        Assert.AreEqual("blazor-abc", request.SessionId);
    }

    private sealed class RecordingObserver : ITurnObserver
    {
        public List<RunStarted> Started { get; } = [];
        public List<RunHeartbeat> Heartbeats { get; } = [];
        public List<TaskListSnapshot> Snapshots { get; } = [];
        public List<TaskListDelta> Deltas { get; } = [];

        public void OnRunStarted(RunStarted run) => Started.Add(run);
        public void OnRunHeartbeat(RunHeartbeat heartbeat) => Heartbeats.Add(heartbeat);
        public void OnTaskListSnapshot(TaskListSnapshot snapshot) => Snapshots.Add(snapshot);
        public void OnTaskListDelta(TaskListDelta delta) => Deltas.Add(delta);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
