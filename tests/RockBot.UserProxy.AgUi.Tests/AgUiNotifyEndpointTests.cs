using System.Net;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// <c>GET /agui/notify</c> over the real pipeline: the fake agent publishes unsolicited traffic on
/// the broadcast topics, the proxy's own <see cref="UserProxyService"/> hands it to the routing
/// frontend, and an HTTP client reads it as server-sent events.
/// </summary>
[TestClass]
public sealed class AgUiNotifyEndpointTests
{
    private const string Thread = "agui-session";
    private static readonly TimeSpan Patience = NotifyStream.Patience;

    private AgUiTestHost _host = null!;
    private HttpClient _client = null!;
    private FakeAgent _agent = null!;

    [TestInitialize]
    public Task Init() => StartAsync();

    [TestCleanup]
    public async Task Cleanup()
    {
        await _agent.DisposeAsync();
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private async Task StartAsync(params (string Key, string Value)[] settings)
    {
        _host = new AgUiTestHost(settings);
        _client = _host.CreateClient();
        _agent = await FakeAgent.StartAsync(_host);
    }

    private async Task RestartAsync(params (string Key, string Value)[] settings)
    {
        await Cleanup();
        await StartAsync(settings);
    }

    private static AgentReply Reply(string content, string sessionId = Thread, bool isFinal = true) => new()
    {
        Content = content, SessionId = sessionId, AgentName = AgUiTestHost.AgentName, IsFinal = isFinal,
    };

    private static string[] Types(IEnumerable<NotifyStream.Frame> frames) => [.. frames.Select(f => f.Type)];

    // ── Refusals ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Notify_WithoutAThreadId_Is400()
    {
        using var response = await _client.GetAsync("/agui/notify");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreNotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Notify_ForAThreadThatIsNotAllowed_Is403()
    {
        using var response = await _client.GetAsync("/agui/notify?threadId=blazor-session");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── The stream ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Notify_IsAnEventStream_ThatStartsWithConnected_AndNoRunStarted()
    {
        using var stream = await NotifyStream.OpenAsync(_client);

        Assert.AreEqual("text/event-stream", stream.Response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("no-cache", stream.Response.Headers.CacheControl?.ToString());
        Assert.AreEqual("no", string.Join(",", stream.Response.Headers.GetValues("X-Accel-Buffering")));

        var connected = await stream.ReadAsync();
        Assert.AreEqual("CUSTOM", connected.Type);
        Assert.AreEqual(TurnEventMapper.CustomNames.Connected, connected.Name);
        Assert.AreEqual(Thread, connected.Value.GetProperty("threadId").GetString());
        Assert.AreEqual(0, connected.Sequence);
    }

    [TestMethod]
    public async Task AnUnsolicitedReply_ArrivesAsATextMessageWithItsOrigin()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        var connected = await stream.ReadConnectedAsync();

        await _agent.BroadcastAsync(Reply("The research is done.") with
        {
            Source = ReplySource.Primary,
            Origin = new ReplyOrigin("agui", "Research the vendors", DateTimeOffset.UtcNow.AddMinutes(-10), Thread),
        });

        var frames = await stream.ReadAsync(4);
        CollectionAssert.AreEqual(new[] { "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM" }, Types(frames));
        Assert.AreEqual("The research is done.", Sse.Str(frames[1].Event, "delta"));
        Assert.AreEqual(TurnEventMapper.CustomNames.Origin, frames[3].Name);
        Assert.AreEqual("Research the vendors", frames[3].Value.GetProperty("origin").GetProperty("promptSummary").GetString());
        Assert.AreEqual(Thread, frames[3].Value.GetProperty("sessionId").GetString());
        Assert.AreEqual(Sse.Str(frames[0].Event, "messageId"), frames[3].Value.GetProperty("messageId").GetString());

        // Ids count up from the connected event's, in the hub's epoch.
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, frames.Select(f => f.Sequence).ToArray());
        var epoch = connected.Id[..connected.Id.LastIndexOf('-')];
        Assert.IsTrue(frames.All(f => f.Id.StartsWith(epoch + "-", StringComparison.Ordinal)));

        // Every event is one the SDK can read back as its own type.
        foreach (var frame in frames)
        {
            var parsed = JsonSerializer.Deserialize(frame.Event.GetRawText(), AGUIJsonSerializerContext.Default.BaseEvent);
            Assert.AreEqual(frame.Type, parsed?.Type);
        }
    }

    [TestMethod]
    public async Task SubagentWork_ArrivesAsLifecycleProgressAndResult()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        var source = ReplySource.Subagent("t1");
        await _agent.EventAsync(new SubagentStarted
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Subagent, Description = "Research the vendors", ThreadId = Thread,
            StartedAt = DateTimeOffset.UtcNow,
        });
        Assert.AreEqual("SUBAGENT_STARTED", (await stream.ReadAsync()).Type);

        // Subagent tool progress keeps the agent's working-memory form of the session id.
        await _agent.BroadcastAsync(Reply("Reading the price list", $"session/{Thread}", isFinal: false) with
        {
            AgentName = "subagent-t1", Source = source,
        });
        var progress = await stream.ReadAsync();
        Assert.AreEqual("ACTIVITY_SNAPSHOT", progress.Type);
        Assert.AreEqual("Reading the price list", progress.Event.GetProperty("content").GetProperty("text").GetString());
        Assert.AreEqual("t1", Sse.Str(progress.Event, "subagentRunId"));

        // Its result, as SubagentResultHandler sends it: non-final, marked as the completion.
        await _agent.BroadcastAsync(Reply("Vendor A is cheaper.", isFinal: false) with
        {
            AgentName = "subagent-t1", Source = source, IsCompletion = true,
        });
        var result = await stream.ReadAsync(5);
        CollectionAssert.AreEqual(
            new[] { "ACTIVITY_SNAPSHOT", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM" }, Types(result));
        Assert.IsTrue(result[0].Event.GetProperty("content").GetProperty("done").GetBoolean());
        Assert.AreEqual("Vendor A is cheaper.", Sse.Str(result[2].Event, "delta"));

        await _agent.EventAsync(new SubagentFinished
        {
            SubagentRunId = "t1", Kind = ReplySourceKind.Subagent, ThreadId = Thread, Outcome = SubagentOutcome.Success,
            FinishedAt = DateTimeOffset.UtcNow,
        });
        Assert.AreEqual("SUBAGENT_FINISHED", (await stream.ReadAsync()).Type);
    }

    [TestMethod]
    public async Task AQuestionFormAndItsStatus_ArriveAsCustomEvents()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        var form = new McpQuestionForm
        {
            QuestionId = "q1", ServerName = "scheduler", ToolName = "create_event", Message = "Which time zone?",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Fields = [new McpQuestionField { Name = "tz", Kind = McpQuestionFieldKind.String, Required = true }],
        };
        await _agent.BroadcastAsync(Reply("The scheduler asks: Which time zone?") with
        {
            ContentType = McpQuestionForm.ContentType,
            StructuredData = JsonSerializer.Serialize(form, TurnEventMapper.PayloadJson),
            Source = ReplySource.Bridge,
        });

        var question = await stream.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestion, question.Name);
        Assert.AreEqual("q1", question.Value.GetProperty("form").GetProperty("questionId").GetString());
        Assert.AreEqual("Which time zone?", question.Value.GetProperty("form").GetProperty("message").GetString());

        await _agent.BroadcastAsync(new McpQuestionStatus
        {
            SessionId = Thread, QuestionId = "q1", State = McpQuestionState.Answered, AnsweredBy = "user",
        });

        var status = await stream.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.McpQuestionStatus, status.Name);
        Assert.AreEqual("q1", status.Value.GetProperty("questionId").GetString());
        Assert.AreEqual("answered", status.Value.GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task TrafficWithNoSession_AndScheduledOutput_ReachTheThread_ButAnotherSessionsDoesNot()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        await _agent.BroadcastAsync(Reply("Not for this client.", "blazor-session"));
        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Rocky" });
        await _agent.BroadcastAsync(new McpSignInStatus { ServerName = "calendar", State = McpSignInState.Required });
        await _agent.BroadcastAsync(Reply("Your morning summary.", "scheduled") with { Source = ReplySource.Scheduled });

        var frames = await stream.ReadAsync(6);
        Assert.AreEqual(TurnEventMapper.CustomNames.AgentName, frames[0].Name);
        Assert.AreEqual("Rocky", frames[0].Value.GetProperty("agentName").GetString());
        Assert.AreEqual(TurnEventMapper.CustomNames.McpSignInStatus, frames[1].Name);
        Assert.AreEqual("Your morning summary.", Sse.Str(frames[3].Event, "delta"));
        // The thread's stream carries other sessions' output too, so the origin says which
        // session each reply was sent in: the only way to tell a scheduled task's from a system job's.
        Assert.AreEqual(TurnEventMapper.CustomNames.Origin, frames[5].Name);
        Assert.AreEqual("scheduled", frames[5].Value.GetProperty("sessionId").GetString());
        // Nothing was skipped in the numbering either: the other session's reply never got a number.
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, frames.Select(f => f.Sequence).ToArray());
    }

    [TestMethod]
    public async Task EachThread_SeesOnlyItsOwnTraffic()
    {
        await RestartAsync(("AgUi:AllowedThreadIds:0", "phone"), ("AgUi:AllowedThreadIds:1", "laptop"));
        using var phone = await NotifyStream.OpenAsync(_client, "phone");
        using var laptop = await NotifyStream.OpenAsync(_client, "laptop");
        await phone.ReadConnectedAsync();
        await laptop.ReadConnectedAsync();

        await _agent.BroadcastAsync(Reply("For the laptop.", "laptop"));
        await _agent.BroadcastAsync(Reply("For the phone.", "phone"));

        Assert.AreEqual("For the phone.", Sse.Str((await phone.ReadAsync(2))[1].Event, "delta"));
        Assert.AreEqual("For the laptop.", Sse.Str((await laptop.ReadAsync(2))[1].Event, "delta"));
    }

    [TestMethod]
    public async Task TwoClientsOnOneThread_BothGetEveryEvent()
    {
        using var first = await NotifyStream.OpenAsync(_client);
        using var second = await NotifyStream.OpenAsync(_client);
        await first.ReadConnectedAsync();
        await second.ReadConnectedAsync();

        await _agent.BroadcastAsync(Reply("Hello both."));

        var a = await first.ReadAsync(4);
        var b = await second.ReadAsync(4);
        CollectionAssert.AreEqual(a.Select(f => f.Id).ToArray(), b.Select(f => f.Id).ToArray());
    }

    [TestMethod]
    public async Task AnIdleStream_IsKeptAliveWithComments()
    {
        await RestartAsync(("AgUi:KeepAliveInterval", "00:00:00.050"));
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        await Task.Delay(400);
        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Rocky" });

        Assert.AreEqual(TurnEventMapper.CustomNames.AgentName, (await stream.ReadAsync()).Name);
        Assert.IsTrue(stream.Comments > 0, "keepalive comments were written while nothing happened");
    }

    // ── Reconnecting ─────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(true, DisplayName = "Last-Event-ID header")]
    [DataRow(false, DisplayName = "lastEventId query parameter")]
    public async Task Reconnecting_WithTheLastId_ReplaysWhatWasMissed_ThenCarriesOnLive(bool header)
    {
        string lastSeen;
        using (var stream = await NotifyStream.OpenAsync(_client))
        {
            await stream.ReadConnectedAsync();
            await _agent.BroadcastAsync(Reply("First."));
            lastSeen = (await stream.ReadAsync(4))[^1].Id;
        }

        // Nobody is connected: these wait in the buffer.
        await _agent.BroadcastAsync(Reply("Second."));
        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Rocky" });

        using var again = header
            ? await NotifyStream.OpenAsync(_client, lastEventId: lastSeen)
            : await NotifyStream.OpenAsync(_client, query: $"&lastEventId={Uri.EscapeDataString(lastSeen)}");
        var missed = await again.ReadAsync(5);
        CollectionAssert.AreEqual(
            new[] { "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "CUSTOM", "CUSTOM" }, Types(missed));
        Assert.AreEqual("Second.", Sse.Str(missed[1].Event, "delta"));
        Assert.AreEqual(TurnEventMapper.CustomNames.AgentName, missed[4].Name);
        CollectionAssert.AreEqual(new long[] { 5, 6, 7, 8, 9 }, missed.Select(f => f.Sequence).ToArray());

        await _agent.BroadcastAsync(Reply("Third."));
        var live = await again.ReadAsync(4);
        Assert.AreEqual("Third.", Sse.Str(live[1].Event, "delta"));
        Assert.AreEqual(10, live[0].Sequence, "live events follow the replay with no gap");
    }

    [TestMethod]
    public async Task Reconnecting_WithAnIdOlderThanTheBuffer_IsToldToResync()
    {
        await RestartAsync(("AgUi:NotifyBufferSize", "3"));
        string connectedId;
        using (var stream = await NotifyStream.OpenAsync(_client))
            connectedId = (await stream.ReadConnectedAsync()).Id;

        for (var i = 1; i <= 5; i++)
            await _agent.BroadcastAsync(new AgentNameChanged { AgentName = $"Name {i}" });
        // The events are buffered once a new stream reports the fifth as its position.
        await WaitForSequenceAsync(5);

        using var again = await NotifyStream.OpenAsync(_client, lastEventId: connectedId);
        var resync = await again.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.Resync, resync.Name);
        Assert.AreEqual(NotifyHub.ResyncReasons.Expired, resync.Value.GetProperty("reason").GetString());
        Assert.AreEqual(Thread, resync.Value.GetProperty("threadId").GetString());
        Assert.AreEqual(5, resync.Sequence);

        // After the resync the stream is live, from that position.
        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Name 6" });
        var next = await again.ReadAsync();
        Assert.AreEqual("Name 6", next.Value.GetProperty("agentName").GetString());
        Assert.AreEqual(6, next.Sequence);
    }

    [TestMethod]
    public async Task Reconnecting_AfterTheProxyRestarted_IsToldToResync()
    {
        string lastSeen;
        using (var stream = await NotifyStream.OpenAsync(_client))
        {
            await stream.ReadConnectedAsync();
            await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Rocky" });
            lastSeen = (await stream.ReadAsync()).Id;
        }

        await RestartAsync();
        // The new process has an event number 1 too; the old id must not be taken for it.
        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Someone else" });
        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "And again" });
        await WaitForSequenceAsync(2);

        using var again = await NotifyStream.OpenAsync(_client, lastEventId: lastSeen);
        var resync = await again.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.Resync, resync.Name);
        Assert.AreEqual(NotifyHub.ResyncReasons.Unknown, resync.Value.GetProperty("reason").GetString());
        Assert.AreNotEqual(lastSeen[..lastSeen.LastIndexOf('-')], resync.Id[..resync.Id.LastIndexOf('-')], "a new epoch");
    }

    /// <summary>Waits until the thread's stream has published <paramref name="sequence"/> events.</summary>
    private async Task WaitForSequenceAsync(long sequence)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            using (var probe = await NotifyStream.OpenAsync(_client))
            {
                if ((await probe.ReadConnectedAsync()).Sequence >= sequence)
                    return;
            }
            Assert.IsTrue(DateTime.UtcNow < deadline, $"the stream never reached event {sequence}");
            await Task.Delay(10);
        }
    }

    // ── Long turns ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ARunThatTimesOut_EndsWithReplyPending_AndItsLateReplyArrivesHere()
    {
        await RestartAsync(("UserProxy:ReplyTimeout", "00:00:00.300"));
        _agent.OnMessage = (_, _) => Task.CompletedTask;
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        using var response = await _client.PostAsync("/agui", new StringContent(
            """{"threadId":"agui-session","runId":"run-1","messages":[{"id":"m1","role":"user","content":"A long job"}]}""",
            Encoding.UTF8, "application/json"));
        var run = Sse.Events(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(TurnEventMapper.CustomNames.ReplyPending, Sse.Str(run[^2], "name"));
        Assert.AreEqual("RUN_FINISHED", Sse.Type(run[^1]));

        // The agent was working all along. Its run events and reply still carry the run's
        // correlation id, on the proxy's own reply topic, but no run is waiting for them.
        var received = _agent.Messages.Single();
        await _agent.RunStartedAsync(received);
        await _agent.ReplyAsync(received, "Still going", isFinal: false);
        await _agent.ReplyAsync(received, "Here is the long answer.");
        await _agent.RunFinishedAsync(received, RunOutcome.Success);

        var frames = await stream.ReadAsync(7);
        Assert.AreEqual(TurnEventMapper.CustomNames.AgentRunStarted, frames[0].Name);
        Assert.AreEqual("run-1", frames[0].Value.GetProperty("runId").GetString());
        Assert.AreEqual("ACTIVITY_SNAPSHOT", frames[1].Type);
        CollectionAssert.AreEqual(
            new[] { "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END" }, Types(frames.Skip(2).Take(3)));
        Assert.AreEqual("Here is the long answer.", Sse.Str(frames[3].Event, "delta"));
        Assert.AreEqual(TurnEventMapper.CustomNames.Origin, frames[5].Name);
        Assert.AreEqual(TurnEventMapper.CustomNames.RunFinished, frames[6].Name);
        Assert.AreEqual("success", frames[6].Value.GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task ARunsOwnTraffic_DoesNotAlsoAppearOnTheNotifyStream()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        using var response = await _client.PostAsync("/agui", new StringContent(
            """{"threadId":"agui-session","runId":"run-1","messages":[{"id":"m1","role":"user","content":"Hello"}]}""",
            Encoding.UTF8, "application/json"));
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Echo: Hello");

        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Rocky" });
        var next = await stream.ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.AgentName, next.Name);
        Assert.AreEqual(1, next.Sequence, "the run's reply was never numbered on this stream");
    }

    // ── Ending ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task ProxyShutdown_EndsTheStream()
    {
        using var stream = await NotifyStream.OpenAsync(_client);
        await stream.ReadConnectedAsync();

        _host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        Assert.IsNull(await stream.NextAsync(), "the stream ends rather than holding up the shutdown");
    }

    [TestMethod]
    public async Task ClosingTheConnection_Unsubscribes_AndLaterEventsStillReachTheBuffer()
    {
        string connectedId;
        using (var stream = await NotifyStream.OpenAsync(_client))
            connectedId = (await stream.ReadConnectedAsync()).Id;

        await _agent.BroadcastAsync(new AgentNameChanged { AgentName = "Rocky" });

        using var again = await NotifyStream.OpenAsync(_client, lastEventId: connectedId);
        Assert.AreEqual("Rocky", (await again.ReadAsync()).Value.GetProperty("agentName").GetString());
    }
}
