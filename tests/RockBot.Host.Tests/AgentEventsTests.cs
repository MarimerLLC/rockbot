using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using RockBot.Messaging;
using RockBot.UserProxy;

namespace RockBot.Host.Tests;

/// <summary>
/// Structured events for user proxies (#795): tool calls, routing, task lists and the turn lifecycle.
/// </summary>
[TestClass]
public sealed class AgentEventsTests
{
    private static readonly AgentIdentity Agent = new("test-agent");

    private static ToolProgressContext Context(
        string? correlationId = "corr-1", bool eventsEnabled = true, ReplySource? source = null) => new()
    {
        SessionId = "session/s1",
        AgentName = "test-agent",
        CorrelationId = correlationId,
        ReplyTo = "user.response.test-agent.proxy",
        EventsEnabled = eventsEnabled,
        Source = source,
    };

    // ── ToolProgressNotifier ────────────────────────────────────────────────────

    [TestMethod]
    public async Task ToolProgressNotifier_PublishesToolCallAndSource_AndKeepsContent()
    {
        var publisher = new RecordingPublisher();
        var notifier = new ToolProgressNotifier(publisher, NullLogger<ToolProgressNotifier>.Instance);
        using var _ = ToolProgressNotifier.SetContext(Context(source: ReplySource.Subagent("t1")));

        await notifier.OnToolCallAsync(new ToolCallInfo
        {
            ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started, ArgsSummary = "q=x",
        }, CancellationToken.None);
        await notifier.OnToolCallAsync(new ToolCallInfo
        {
            ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Finished, Result = "3 hits", DurationMs = 42,
        }, CancellationToken.None);
        await notifier.OnToolCallAsync(new ToolCallInfo
        {
            ToolCallId = "c2", ToolCallName = "fetch", Phase = ToolCallPhase.Failed, Error = "404",
        }, CancellationToken.None);

        var replies = publisher.Payloads<AgentReply>();
        Assert.AreEqual("Calling web_search(q=x)…", replies[0].Content);
        Assert.AreEqual("web_search → 3 hits", replies[1].Content);
        Assert.AreEqual("fetch failed: 404", replies[2].Content);
        Assert.AreEqual("c1", replies[1].ToolCall?.ToolCallId);
        Assert.AreEqual(42, replies[1].ToolCall?.DurationMs);
        Assert.AreEqual(ToolCallPhase.Failed, replies[2].ToolCall?.Phase);
        Assert.IsTrue(replies.All(r => r.Source?.Kind == ReplySourceKind.Subagent && r.Source.TaskId == "t1"));
        Assert.IsTrue(replies.All(r => !r.IsFinal));
        Assert.AreEqual("corr-1", publisher.Published[0].Envelope.CorrelationId);
    }

    [TestMethod]
    public async Task ToolProgressNotifier_WithoutASource_ReportsPrimary()
    {
        var publisher = new RecordingPublisher();
        var notifier = new ToolProgressNotifier(publisher, NullLogger<ToolProgressNotifier>.Instance);
        using var _ = ToolProgressNotifier.SetContext(Context());

        await notifier.OnToolInvokingAsync("x", null, CancellationToken.None);

        Assert.AreEqual(ReplySourceKind.Primary, publisher.Payloads<AgentReply>().Single().Source?.Kind);
    }

    [TestMethod]
    public void SetContext_DisposeRestoresThePreviousContext()
    {
        var outer = Context(correlationId: "outer");
        var inner = Context(correlationId: "inner");

        using (ToolProgressNotifier.SetContext(outer))
        {
            using (ToolProgressNotifier.SetContext(inner))
                Assert.AreSame(inner, ToolProgressNotifier.Current);

            Assert.AreSame(outer, ToolProgressNotifier.Current, "a nested run hands the context back");
        }

        Assert.IsNull(ToolProgressNotifier.Current);
    }

    [TestMethod]
    public void ToolProgressContext_ThreadIdIsTheRawSessionId()
    {
        Assert.AreEqual("s1", Context().EffectiveThreadId);
    }

    // ── AgentEventPublisher routing ─────────────────────────────────────────────

    [TestMethod]
    public async Task AgentEventPublisher_CorrelatedWithEvents_GoesToReplyTo()
    {
        var publisher = new RecordingPublisher();
        var events = new AgentEventPublisher(publisher, Agent, NullLogger<AgentEventPublisher>.Instance);
        using var _ = ToolProgressNotifier.SetContext(Context());

        await events.PublishForRunAsync(Snapshot());

        var (topic, envelope) = publisher.Published.Single();
        Assert.AreEqual("user.response.test-agent.proxy", topic);
        Assert.AreEqual("corr-1", envelope.CorrelationId);
        Assert.AreEqual(typeof(TaskListSnapshot).FullName, envelope.MessageType);
    }

    [TestMethod]
    public async Task AgentEventPublisher_CorrelatedWithoutEvents_IsDropped()
    {
        var publisher = new RecordingPublisher();
        var events = new AgentEventPublisher(publisher, Agent, NullLogger<AgentEventPublisher>.Instance);
        using var _ = ToolProgressNotifier.SetContext(Context(eventsEnabled: false));

        await events.PublishForRunAsync(Snapshot());

        Assert.AreEqual(0, publisher.Published.Count, "an older proxy would read the event as a reply");
    }

    [TestMethod]
    public async Task AgentEventPublisher_Uncorrelated_IsBroadcast()
    {
        var publisher = new RecordingPublisher();
        var events = new AgentEventPublisher(publisher, Agent, NullLogger<AgentEventPublisher>.Instance);
        using var _ = ToolProgressNotifier.SetContext(Context(correlationId: null, eventsEnabled: false));

        await events.PublishForRunAsync(Snapshot());

        var (topic, envelope) = publisher.Published.Single();
        Assert.AreEqual($"{UserProxyTopics.UserEvent}.test-agent", topic);
        Assert.IsNull(envelope.CorrelationId);
    }

    [TestMethod]
    public async Task AgentEventPublisher_WithoutAContext_IsDropped()
    {
        var publisher = new RecordingPublisher();
        var events = new AgentEventPublisher(publisher, Agent, NullLogger<AgentEventPublisher>.Instance);

        await events.PublishForRunAsync(Snapshot());

        Assert.AreEqual(0, publisher.Published.Count);
    }

    private static TaskListSnapshot Snapshot() => new()
    {
        ThreadId = "s1", Source = ReplySource.Primary, Items = [],
    };

    // ── AgentTaskListTools ──────────────────────────────────────────────────────

    [TestMethod]
    public void AgentTaskListTools_PublishesASnapshotOnCreateAndADeltaOnUpdate()
    {
        var events = new RecordingEventPublisher();
        var tools = new AgentTaskListTools(new AgentTaskList(), NullLogger.Instance, events);
        using var _ = ToolProgressNotifier.SetContext(Context(source: ReplySource.Subagent("t9")));

        tools.TaskCreate(["Search", "Summarize"]);
        tools.TaskUpdate(1, "in_progress");

        var snapshot = events.Events.OfType<TaskListSnapshot>().Single();
        Assert.AreEqual(2, snapshot.Items.Count);
        Assert.AreEqual("1", snapshot.Items[0].Id);
        Assert.AreEqual("pending", snapshot.Items[0].Status);
        Assert.AreEqual("s1", snapshot.ThreadId);
        Assert.AreEqual("corr-1", snapshot.RunId);
        Assert.AreEqual("t9", snapshot.Source.TaskId);

        var delta = events.Events.OfType<TaskListDelta>().Single();
        Assert.AreEqual("1", delta.Item.Id);
        Assert.AreEqual("in_progress", delta.Item.Status);
    }

    [TestMethod]
    public void AgentTaskListTools_UncorrelatedSubagentList_IsPublished()
    {
        var events = new RecordingEventPublisher();
        var tools = new AgentTaskListTools(new AgentTaskList(), NullLogger.Instance, events);
        using var _ = ToolProgressNotifier.SetContext(Context(correlationId: null, source: ReplySource.Subagent("t9")));

        tools.TaskCreate(["Search"]);

        Assert.AreEqual("t9", events.Events.OfType<TaskListSnapshot>().Single().Source.TaskId);
    }

    [TestMethod]
    [DataRow(ReplySourceKind.Scheduled)]
    [DataRow(ReplySourceKind.A2A)]
    [DataRow(ReplySourceKind.Primary, DisplayName = "uncorrelated primary (a subagent-result synthesis)")]
    public void AgentTaskListTools_BackgroundRunLists_StayPrivate(string kind)
    {
        // A proxy would show these as the user's plan.
        var events = new RecordingEventPublisher();
        var tools = new AgentTaskListTools(new AgentTaskList(), NullLogger.Instance, events);
        using var _ = ToolProgressNotifier.SetContext(Context(correlationId: null, source: new ReplySource { Kind = kind }));

        tools.TaskCreate(["Search"]);
        tools.TaskUpdate(1, "completed");

        Assert.AreEqual(0, events.Events.Count);
    }

    [TestMethod]
    public async Task AgentTaskListTools_PublishesInOrder_EvenWhenAPublishIsSlow()
    {
        var events = new SlowFirstEventPublisher();
        var tools = new AgentTaskListTools(new AgentTaskList(), NullLogger.Instance, events);
        using var _ = ToolProgressNotifier.SetContext(Context());

        tools.TaskCreate(["Search", "Summarize"]);
        tools.TaskUpdate(1, "in_progress");
        tools.TaskUpdate(1, "completed");
        events.ReleaseFirst();
        await tools.WhenPublishedAsync().WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(
            new[] { nameof(TaskListSnapshot), nameof(TaskListDelta), nameof(TaskListDelta) },
            events.Published.Select(e => e.GetType().Name).ToArray());
        Assert.AreEqual("completed", ((TaskListDelta)events.Published[^1]).Item.Status);
    }

    [TestMethod]
    public async Task AgentTaskListTools_FlushOnDispose_WaitsForPendingEvents()
    {
        // RunAsync holds this for the run, so the final reply can't overtake the last task-list
        // event on the turn's queue: a proxy drops events for a turn that has already ended (#797).
        var events = new SlowFirstEventPublisher();
        var tools = new AgentTaskListTools(new AgentTaskList(), NullLogger.Instance, events);
        using var _ = ToolProgressNotifier.SetContext(Context());

        var flush = tools.FlushOnDispose(TimeSpan.FromSeconds(5));
        tools.TaskCreate(["Search"]);
        tools.TaskUpdate(1, "completed");

        var disposing = flush.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.IsFalse(disposing.IsCompleted, "the run must not end while a task-list event is unpublished");

        events.ReleaseFirst();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(
            new[] { nameof(TaskListSnapshot), nameof(TaskListDelta) },
            events.Published.Select(e => e.GetType().Name).ToArray());
    }

    [TestMethod]
    public async Task AgentTaskListTools_FlushOnDispose_GivesUpAfterItsTimeout()
    {
        // A stuck publish must not hold up the user's reply for long.
        var events = new SlowFirstEventPublisher();
        var tools = new AgentTaskListTools(new AgentTaskList(), NullLogger.Instance, events);
        using var _ = ToolProgressNotifier.SetContext(Context());

        tools.TaskCreate(["Search"]);
        await tools.FlushOnDispose(TimeSpan.FromMilliseconds(50)).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, events.Published.Count);
        events.ReleaseFirst();
    }

    [TestMethod]
    public void AgentTaskListTools_FailedUpdate_PublishesNothing()
    {
        var events = new RecordingEventPublisher();
        var tools = new AgentTaskListTools(new AgentTaskList(), NullLogger.Instance, events);
        using var _ = ToolProgressNotifier.SetContext(Context());

        tools.TaskUpdate(7, "completed");

        Assert.AreEqual(0, events.Events.Count);
    }

    // ── Tool calls from the loops ───────────────────────────────────────────────

    [TestMethod]
    [DataRow(false, DisplayName = "native (RockBotFunctionInvokingChatClient)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task ToolCall_ReportsStartedThenFinished_WithTheCallIdAndDuration(bool textBased)
    {
        var notifier = new RecordingNotifier();
        var model = new ConsequentialActionGateTests.ToolThenTextChatClient(
            new FunctionCallContent("call-7", "lookup", new Dictionary<string, object?> { ["q"] = "x" }), "Done.");
        var runner = ConsequentialActionGateTests.CreateRunner(model, textBased, new AgentHostOptions(),
            progressNotifier: notifier);
        var options = new ChatOptions { Tools = [AIFunctionFactory.Create((string? q = null) => "found it", "lookup")] };

        await runner.RunAsync(ConsequentialActionGateTests.Conversation("look it up"), options, "s1",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        var calls = notifier.Calls.Where(c => c.ToolCallName == "lookup").ToList();
        Assert.AreEqual(2, calls.Count, string.Join(", ", calls.Select(c => c.Phase)));
        Assert.AreEqual(ToolCallPhase.Started, calls[0].Phase);
        Assert.AreEqual(ToolCallPhase.Finished, calls[1].Phase);
        Assert.IsTrue(calls.All(c => c.ToolCallId == "call-7"));
        Assert.IsNotNull(calls[1].DurationMs);
        Assert.AreEqual("found it", calls[1].Result);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "native (RockBotFunctionInvokingChatClient)")]
    [DataRow(true, DisplayName = "text-based loop")]
    public async Task ToolCall_ThatThrows_ReportsFailed(bool textBased)
    {
        var notifier = new RecordingNotifier();
        var model = new ConsequentialActionGateTests.ToolThenTextChatClient(
            new FunctionCallContent("call-8", "explode", new Dictionary<string, object?>()), "Done.");
        var runner = ConsequentialActionGateTests.CreateRunner(model, textBased, new AgentHostOptions(),
            progressNotifier: notifier);
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(string () => throw new InvalidOperationException("boom"), "explode")],
        };

        await runner.RunAsync(ConsequentialActionGateTests.Conversation("do it"), options, "s1",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        var last = notifier.Calls.Last(c => c.ToolCallName == "explode");
        Assert.AreEqual(ToolCallPhase.Failed, last.Phase);
        Assert.AreEqual("call-8", last.ToolCallId);
        StringAssert.Contains(last.Error, "boom");
    }

    [TestMethod]
    public async Task ToolCall_SetsTheCurrentCallIdWhileTheToolRuns()
    {
        string? seen = null;
        var model = new ConsequentialActionGateTests.ToolThenTextChatClient(
            new FunctionCallContent("call-9", "spawn", new Dictionary<string, object?>()), "Done.");
        var runner = ConsequentialActionGateTests.CreateRunner(model, textBased: false, new AgentHostOptions(),
            progressNotifier: new RecordingNotifier());
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(string () => { seen = CurrentToolCallContext.CallId; return "ok"; }, "spawn")],
        };

        await runner.RunAsync(ConsequentialActionGateTests.Conversation("spawn"), options, "s1",
            enableFollowUp: false, enableCompletionEval: false, cancellationToken: CancellationToken.None);

        Assert.AreEqual("call-9", seen);
        Assert.IsNull(CurrentToolCallContext.CallId);
    }

    // ── TurnLifecycle ───────────────────────────────────────────────────────────

    private static TurnLifecycle Turn(RecordingPublisher publisher, FakeTimeProvider time,
        bool eventsEnabled = true, string? correlationId = "corr-1") =>
        new(publisher, "test-agent", "s1", correlationId, "user.response.test-agent.proxy",
            eventsEnabled, TimeSpan.FromSeconds(15), NullLogger.Instance, time);

    [TestMethod]
    public async Task TurnLifecycle_SendsStartedHeartbeatsAndFinished()
    {
        var publisher = new RecordingPublisher();
        var time = new FakeTimeProvider();
        await using var turn = Turn(publisher, time);

        await turn.StartAsync();
        turn.UpdateStatus("Calling web_search…");
        time.Advance(TimeSpan.FromSeconds(15));
        await WaitForAsync(() => publisher.Payloads<RunHeartbeat>().Count == 1);
        time.Advance(TimeSpan.FromSeconds(15));
        await WaitForAsync(() => publisher.Payloads<RunHeartbeat>().Count == 2);
        await turn.FinishAsync(RunOutcome.Success);

        var started = publisher.Payloads<RunStarted>().Single();
        Assert.AreEqual("corr-1", started.RunId);
        Assert.AreEqual("s1", started.ThreadId);
        Assert.AreEqual(15, started.HeartbeatIntervalSeconds);
        var heartbeats = publisher.Payloads<RunHeartbeat>();
        Assert.AreEqual("Calling web_search…", heartbeats[0].Status);
        Assert.AreEqual(30, heartbeats[1].ElapsedSeconds, 0.5);
        Assert.AreEqual(RunOutcome.Success, publisher.Payloads<RunFinished>().Single().Outcome);
        Assert.IsTrue(publisher.Published.All(p => p.Topic == "user.response.test-agent.proxy"
                                                   && p.Envelope.CorrelationId == "corr-1"));

        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(20);
        Assert.AreEqual(2, publisher.Payloads<RunHeartbeat>().Count, "no heartbeats after the run finished");
    }

    [TestMethod]
    public async Task TurnLifecycle_FinishIsIdempotent()
    {
        var publisher = new RecordingPublisher();
        var turn = Turn(publisher, new FakeTimeProvider());

        await turn.StartAsync();
        await turn.FinishAsync(RunOutcome.Cancelled);
        await turn.FinishAsync(RunOutcome.Success);
        await turn.DisposeAsync();

        Assert.AreEqual(RunOutcome.Cancelled, publisher.Payloads<RunFinished>().Single().Outcome);
    }

    [TestMethod]
    public async Task TurnLifecycle_DisposedWithoutFinishing_ReportsAnError()
    {
        var publisher = new RecordingPublisher();
        var turn = Turn(publisher, new FakeTimeProvider());

        await turn.StartAsync();
        await turn.DisposeAsync();

        var finished = publisher.Payloads<RunFinished>().Single();
        Assert.AreEqual(RunOutcome.Error, finished.Outcome);
        Assert.IsNotNull(finished.Error);
    }

    [TestMethod]
    [DataRow(false, "corr-1", DisplayName = "proxy did not ask for events")]
    [DataRow(true, null, DisplayName = "no correlation id")]
    public async Task TurnLifecycle_Disabled_PublishesNothing(bool eventsEnabled, string? correlationId)
    {
        var publisher = new RecordingPublisher();
        var time = new FakeTimeProvider();
        var turn = Turn(publisher, time, eventsEnabled, correlationId);

        await turn.StartAsync();
        time.Advance(TimeSpan.FromMinutes(1));
        await turn.FinishAsync(RunOutcome.Success);

        Assert.IsFalse(turn.Enabled);
        Assert.AreEqual(0, publisher.Published.Count);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(5);
        Assert.IsTrue(condition(), "condition not met in time");
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────

    private sealed class RecordingPublisher : IMessagePublisher
    {
        private readonly List<(string Topic, MessageEnvelope Envelope)> _published = [];

        public List<(string Topic, MessageEnvelope Envelope)> Published
        {
            get { lock (_published) return [.. _published]; }
        }

        public Task PublishAsync(string topic, MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            lock (_published) _published.Add((topic, envelope));
            return Task.CompletedTask;
        }

        public List<T> Payloads<T>() => Published
            .Where(p => p.Envelope.MessageType == typeof(T).FullName)
            .Select(p => p.Envelope.GetPayload<T>()!)
            .ToList();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingEventPublisher : IAgentEventPublisher
    {
        public List<object> Events { get; } = [];

        public Task PublishForRunAsync<T>(T evt, ToolProgressContext context, CancellationToken ct = default) where T : class
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }

        public Task BroadcastAsync<T>(T evt, CancellationToken ct = default) where T : class
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds the first publish until released; publishes after it go straight through.</summary>
    private sealed class SlowFirstEventPublisher : IAgentEventPublisher
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public List<object> Published { get; } = [];

        public void ReleaseFirst() => _release.TrySetResult();

        public async Task PublishForRunAsync<T>(T evt, ToolProgressContext context, CancellationToken ct = default) where T : class
        {
            if (Interlocked.Increment(ref _calls) == 1)
                await _release.Task;
            lock (Published) Published.Add(evt);
        }

        public Task BroadcastAsync<T>(T evt, CancellationToken ct = default) where T : class => Task.CompletedTask;
    }

    private sealed class RecordingNotifier : IToolProgressNotifier
    {
        private readonly List<ToolCallInfo> _calls = [];

        public List<ToolCallInfo> Calls { get { lock (_calls) return [.. _calls]; } }

        public Task OnToolInvokingAsync(string toolName, string? argsSummary, CancellationToken ct) => Task.CompletedTask;
        public Task OnToolInvokedAsync(string toolName, string? resultSummary, CancellationToken ct) => Task.CompletedTask;

        public Task OnToolCallAsync(ToolCallInfo info, CancellationToken ct)
        {
            lock (_calls) _calls.Add(info);
            return Task.CompletedTask;
        }
    }
}
