using System.Net;
using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The proxy's endpoints over the real pipeline: an HTTP client on one side, a fake agent on the
/// in-process bus on the other, and the proxy's own <see cref="UserProxyService"/> between them.
/// </summary>
[TestClass]
public sealed class AgUiEndpointTests
{
    private const string Thread = "agui-session";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

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

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static StringContent Run(string runId, string text = "Hello", string threadId = Thread, string more = "") =>
        Json($$"""{"threadId":"{{threadId}}","runId":"{{runId}}","messages":[{"id":"m1","role":"user","content":"{{text}}"}]{{more}}}""");

    private async Task<List<JsonElement>> RunToEndAsync(HttpContent content)
    {
        using var response = await _client.PostAsync("/agui", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return Sse.Events(await response.Content.ReadAsStringAsync());
    }

    private static string[] Types(IEnumerable<JsonElement> events) => [.. events.Select(Sse.Type)];

    private async Task<FakeAgent.ReceivedMessage> AgentReceivedAsync()
    {
        var deadline = DateTime.UtcNow + Patience;
        while (_agent.Messages.Count == 0)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "the agent never received the message");
            await Task.Delay(10);
        }
        return _agent.Messages[0];
    }

    // ── Runs ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Run_StreamsTheReplyBetweenRunStartedAndRunFinished()
    {
        using var response = await _client.PostAsync("/agui", Run("run-1", "Hello"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("no-cache", response.Headers.CacheControl?.ToString());
        Assert.AreEqual("no", string.Join(",", response.Headers.GetValues("X-Accel-Buffering")));

        var events = Sse.Events(await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED" },
            Types(events));
        Assert.AreEqual(Thread, Sse.Str(events[0], "threadId"));
        Assert.AreEqual("run-1", Sse.Str(events[0], "runId"));
        Assert.AreEqual(AGUIProtocol.Version, Sse.Str(events[0], "protocolVersion"));
        Assert.AreEqual("assistant", Sse.Str(events[1], "role"));
        Assert.AreEqual("Echo: Hello", Sse.Str(events[2], "delta"));
        Assert.IsFalse(events[^1].TryGetProperty("outcome", out _), "success is an absent outcome");
    }

    [TestMethod]
    public async Task Run_SendsTheAgentAUserMessageForTheThread()
    {
        await RunToEndAsync(Json("""
            {"threadId":"agui-session","runId":"run-7","messages":[
              {"id":"1","role":"user","content":"earlier"},
              {"id":"2","role":"assistant","content":"an answer"},
              {"id":"3","role":"user","content":"the question"}]}
            """));

        var received = _agent.Messages.Single();
        Assert.AreEqual("the question", received.Message.Content);
        Assert.AreEqual(Thread, received.Message.SessionId);
        Assert.AreEqual("agui-user", received.Message.UserId);
        Assert.AreEqual("agui", received.Message.ChannelName);
        // The proxy service adds AgentEvents to whatever the frontend declares.
        Assert.AreEqual(ClientCapabilityPresets.AgUi | ClientCapabilities.AgentEvents, received.Message.ClientCapabilities);
        // The run id is the turn's correlation id, so the agent's run events line up with it.
        Assert.AreEqual("run-7", received.Envelope.CorrelationId);
        Assert.AreEqual("agui-test", received.Envelope.Source);
    }

    [TestMethod]
    public async Task Run_PassesOnTheCapabilitiesTheClientDeclares()
    {
        await RunToEndAsync(Run("run-1", more: ""","forwardedProps":{"rockbot":{"capabilities":["markdownBasic","InteractiveForms"]}}"""));

        Assert.AreEqual(
            ClientCapabilities.Text | ClientCapabilities.MarkdownBasic | ClientCapabilities.InteractiveForms | ClientCapabilities.AgentEvents,
            _agent.Messages.Single().Message.ClientCapabilities);
    }

    [TestMethod]
    public async Task Run_StreamsProgressAndToolCallsBeforeTheReply()
    {
        _agent.OnMessage = async (agent, received) =>
        {
            var call = new ToolCallInfo
            {
                ToolCallId = "c1", ToolCallName = "web_search", Phase = ToolCallPhase.Started, ArgsSummary = "query: rockbot",
            };
            await agent.RunStartedAsync(received);
            await agent.ReplyAsync(received, "Thinking", isFinal: false);
            await agent.ReplyAsync(received, "Calling web_search…", isFinal: false, toolCall: call);
            await agent.ReplyAsync(received, "done", isFinal: false,
                toolCall: call with { Phase = ToolCallPhase.Finished, Result = "3 results", DurationMs = 120 });
            await agent.SendAsync(received, new TaskListSnapshot
            {
                ThreadId = Thread, RunId = "run-1", Source = ReplySource.Primary,
                Items = [new TaskListItem { Id = "1", Description = "search", Status = "completed" }],
            });
            await agent.ReplyAsync(received, "Found it.");
            await agent.RunFinishedAsync(received, RunOutcome.Success);
        };

        var events = await RunToEndAsync(Run("run-1"));

        CollectionAssert.AreEqual(
            new[]
            {
                "RUN_STARTED", "CUSTOM", "ACTIVITY_SNAPSHOT",
                "TOOL_CALL_START", "TOOL_CALL_ARGS", "TOOL_CALL_END", "TOOL_CALL_RESULT",
                "STATE_SNAPSHOT",
                "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_FINISHED",
            },
            Types(events));
        Assert.AreEqual(TurnEventMapper.CustomNames.AgentRunStarted, Sse.Str(events[1], "name"));
        Assert.AreEqual("Thinking", events[2].GetProperty("content").GetProperty("text").GetString());
        Assert.AreEqual("web_search", Sse.Str(events[3], "toolCallName"));
        Assert.AreEqual("3 results", Sse.Str(events[6], "content"));
        Assert.AreEqual("Found it.", Sse.Str(events[9], "delta"));

        // Every event is one the SDK can read back as its own type.
        foreach (var e in events)
        {
            var parsed = JsonSerializer.Deserialize(e.GetRawText(), AGUIJsonSerializerContext.Default.BaseEvent);
            Assert.AreEqual(Sse.Type(e), parsed?.Type);
        }
    }

    [TestMethod]
    public async Task Run_WhenTheAgentReportsAnError_EndsWithRunError()
    {
        _agent.OnMessage = async (agent, received) =>
        {
            await agent.RunStartedAsync(received);
            await agent.RunFinishedAsync(received, RunOutcome.Error, "the model refused");
            await agent.ReplyAsync(received, "Sorry, that failed.");
        };

        var events = await RunToEndAsync(Run("run-1"));

        CollectionAssert.AreEqual(
            new[] { "RUN_STARTED", "CUSTOM", "TEXT_MESSAGE_START", "TEXT_MESSAGE_CONTENT", "TEXT_MESSAGE_END", "RUN_ERROR" },
            Types(events));
        Assert.AreEqual(TurnEventMapper.ErrorCodes.Error, Sse.Str(events[^1], "code"));
        StringAssert.Contains(Sse.Str(events[^1], "message"), "the model refused");
    }

    [TestMethod]
    public async Task Run_WhenTheAgentDoesNotAnswerInTime_EndsNormallyWithReplyPending()
    {
        await RestartAsync(("UserProxy:ReplyTimeout", "00:00:00.300"));
        _agent.OnMessage = (_, _) => Task.CompletedTask;

        var events = await RunToEndAsync(Run("run-1"));

        // Not an error: the agent may still answer, and the answer then goes to the notify stream.
        CollectionAssert.AreEqual(new[] { "RUN_STARTED", "CUSTOM", "RUN_FINISHED" }, Types(events));
        Assert.AreEqual(TurnEventMapper.CustomNames.ReplyPending, Sse.Str(events[1], "name"));
        Assert.AreEqual("run-1", events[1].GetProperty("value").GetProperty("runId").GetString());
        Assert.IsFalse(events[^1].TryGetProperty("outcome", out _), "the run ends normally");
    }

    [TestMethod]
    public async Task Run_WhenTheAgentGoesSilentAfterStarting_StillEndsWithRunErrorAgentLost()
    {
        _host.Services.GetRequiredService<UserProxyOptions>().HeartbeatTimeout = TimeSpan.FromMilliseconds(200);
        _agent.OnMessage = (agent, received) => agent.RunStartedAsync(received);

        var events = await RunToEndAsync(Run("run-1"));

        Assert.AreEqual("RUN_ERROR", Sse.Type(events[^1]));
        Assert.AreEqual(TurnEventMapper.ErrorCodes.AgentLost, Sse.Str(events[^1], "code"));
        Assert.IsFalse(events.Any(e => Sse.Str(e, "name") == TurnEventMapper.CustomNames.ReplyPending));
    }

    [TestMethod]
    public async Task Run_KeepsAnIdleStreamAliveWithComments()
    {
        await RestartAsync(("AgUi:KeepAliveInterval", "00:00:00.050"));
        _agent.OnMessage = async (agent, received) =>
        {
            await Task.Delay(400);
            await agent.ReplyAsync(received, "Slow answer");
        };

        using var response = await _client.PostAsync("/agui", Run("run-1"));
        var body = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(body, ": keepalive\n\n");
        // Comments are not events: the run is still whole.
        Assert.AreEqual("RUN_FINISHED", Sse.Type(Sse.Events(body)[^1]));
    }

    // ── Cancelling ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task CancelEndpoint_EndsTheOpenRunAsCancelled()
    {
        // No reply follows a cancelled run, so don't wait long for one.
        _host.Services.GetRequiredService<UserProxyOptions>().RunFinishedGracePeriod = TimeSpan.FromMilliseconds(50);
        _agent.OnMessage = (agent, received) => agent.RunStartedAsync(received);
        _agent.OnCancel = (agent, _) => agent.RunFinishedAsync(agent.Messages[0], RunOutcome.Cancelled);

        var run = RunToEndAsync(Run("run-1"));
        await AgentReceivedAsync();

        using var cancel = await _client.PostAsync($"/threads/{Thread}/cancel", content: null);
        Assert.AreEqual(HttpStatusCode.Accepted, cancel.StatusCode);
        Assert.AreEqual(Thread, (await _agent.CancelRequested.WaitAsync(Patience)).SessionId);

        var events = await run.WaitAsync(Patience);
        Assert.AreEqual("RUN_FINISHED", Sse.Type(events[^1]));
        Assert.AreEqual("cancelled", events[^1].GetProperty("outcome").GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task CancelEndpoint_RefusesAThreadThatIsNotAllowed()
    {
        using var response = await _client.PostAsync("/threads/blazor-session/cancel", content: null);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsFalse(_agent.CancelRequested.IsCompleted);
    }

    [TestMethod]
    public async Task ClientAbort_CancelsTheSession()
    {
        _agent.OnMessage = (agent, received) => agent.RunStartedAsync(received);

        using var abort = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/agui") { Content = Run("run-1") };
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await AgentReceivedAsync();
        Assert.IsFalse(_agent.CancelRequested.IsCompleted, "nothing is cancelled while the client is listening");

        await abort.CancelAsync();
        response.Dispose();

        Assert.AreEqual(Thread, (await _agent.CancelRequested.WaitAsync(Patience)).SessionId);
    }

    [TestMethod]
    public async Task ProxyShutdown_EndsTheRunWithRunError_AndLeavesTheSessionAlone()
    {
        _agent.OnMessage = (agent, received) => agent.RunStartedAsync(received);

        var run = RunToEndAsync(Run("run-1"));
        await AgentReceivedAsync();

        _host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();

        var events = await run.WaitAsync(Patience);
        Assert.AreEqual("RUN_ERROR", Sse.Type(events[^1]));
        Assert.AreEqual(AgUiEndpoints.ProxyStoppingCode, Sse.Str(events[^1], "code"));
        // A rollout must not cancel the user's turn: the agent finishes it.
        Assert.IsFalse(_agent.CancelRequested.IsCompleted);
    }

    // ── Refusals, before any event is streamed ───────────────────────────────

    [TestMethod]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[""", DisplayName = "truncated JSON")]
    [DataRow("""[]""", DisplayName = "not an object")]
    [DataRow("""null""", DisplayName = "null")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[]}""", DisplayName = "no user message")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"assistant","content":"hi"}]}""", DisplayName = "only an assistant message")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}],"resume":[{"interruptId":"q1","status":"approved"}]}""", DisplayName = "resume with an unknown status")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[],"resume":[{"interruptId":"q1","status":"resolved","payload":"yes"}]}""", DisplayName = "resume whose payload is not an object")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[],"resume":[{"interruptId":"q1","status":"cancelled"},{"interruptId":"q1","status":"cancelled"}]}""", DisplayName = "resume answering one interrupt twice")]
    [DataRow("""{"threadId":"agui-session","runId":"r1","messages":[{"id":"1","role":"user","content":"hi"}],"forwardedProps":{"rockbot":{"questionMode":"popup"}}}""", DisplayName = "an unknown question mode")]
    public async Task Run_WithABadRequest_Is400_AndReachesNoAgent(string body)
    {
        using var response = await _client.PostAsync("/agui", Json(body));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreNotEqual("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(0, _agent.Messages.Count);
    }

    [TestMethod]
    public async Task Run_OnAThreadThatIsNotAllowed_Is403()
    {
        using var response = await _client.PostAsync("/agui", Run("run-1", threadId: "blazor-session"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual(0, _agent.Messages.Count);
    }

    [TestMethod]
    public async Task Run_OnAConfiguredThread_IsAllowed_AndTheDefaultNoLongerIs()
    {
        await RestartAsync(("AgUi:AllowedThreadIds:0", "phone"), ("AgUi:AllowedThreadIds:1", "laptop"));

        var events = await RunToEndAsync(Run("run-1", threadId: "laptop"));
        Assert.AreEqual("RUN_FINISHED", Sse.Type(events[^1]));
        Assert.AreEqual("laptop", _agent.Messages.Single().Message.SessionId);

        using var refused = await _client.PostAsync("/agui", Run("run-2"));
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [TestMethod]
    public async Task Run_WithTheIdOfARunStillInProgress_Is409()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _agent.OnMessage = async (agent, received) =>
        {
            await release.Task;
            await agent.ReplyAsync(received, "Done");
        };

        var first = RunToEndAsync(Run("run-1"));
        try
        {
            await AgentReceivedAsync();

            using var duplicate = await _client.PostAsync("/agui", Run("run-1"));
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.AreNotEqual("text/event-stream", duplicate.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual(1, _agent.Messages.Count, "the duplicate never reached the agent");
        }
        finally
        {
            // Also on failure, or the fake agent's handler would never return.
            release.TrySetResult();
        }

        Assert.AreEqual("RUN_FINISHED", Sse.Type((await first.WaitAsync(Patience))[^1]));

        // Once the run has ended its id is free again.
        Assert.AreEqual("RUN_FINISHED", Sse.Type((await RunToEndAsync(Run("run-1")))[^1]));
    }

    // ── History ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Messages_ReturnsTheThreadsHistoryAsAMessagesSnapshot()
    {
        _agent.History = new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn { Role = "user", Content = "hi", Timestamp = DateTimeOffset.UtcNow },
                new ConversationHistoryTurn
                {
                    Role = "assistant", Content = "hello", Timestamp = DateTimeOffset.UtcNow, AgentName = "RockBot",
                    Attachments = [new AgentAttachment { Mime = "image/png", Path = "chart.png" }],
                },
            ],
        };

        using var response = await _client.GetAsync($"/threads/{Thread}/messages");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual("MESSAGES_SNAPSHOT", json.GetProperty("type").GetString());
        var messages = json.GetProperty("messages");
        Assert.AreEqual(2, messages.GetArrayLength());
        Assert.AreEqual("user", messages[0].GetProperty("role").GetString());
        Assert.AreEqual("hi", messages[0].GetProperty("content").GetString());
        Assert.AreEqual($"{Thread}-1", messages[1].GetProperty("id").GetString());
        Assert.AreEqual("hello", messages[1].GetProperty("content").GetString());
        Assert.AreEqual("chart.png",
            messages[1].GetProperty("metadata").GetProperty("rockbot").GetProperty("attachments")[0].GetProperty("path").GetString());
    }

    [TestMethod]
    public async Task Messages_ForAThreadThatIsNotAllowed_Is403()
    {
        _agent.History = new ConversationHistoryResponse { Turns = [] };

        using var response = await _client.GetAsync("/threads/blazor-session/messages");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Messages_WhenTheAgentDoesNotAnswer_Is504()
    {
        await RestartAsync(("UserProxy:ReplyTimeout", "00:00:00.300"));

        using var response = await _client.GetAsync($"/threads/{Thread}/messages");

        Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    // ── Health ───────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Healthz_Is200_WhileTheProxyIsOnTheBus_And503Otherwise()
    {
        using (var up = await _client.GetAsync("/healthz"))
            Assert.AreEqual(HttpStatusCode.OK, up.StatusCode);

        await _host.Services.GetRequiredService<UserProxyService>().StopAsync(CancellationToken.None);

        using var down = await _client.GetAsync("/healthz");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, down.StatusCode);

        // A run is refused with a plain error rather than a stream nothing will ever feed.
        using var run = await _client.PostAsync("/agui", Run("run-1"));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, run.StatusCode);
    }
}
