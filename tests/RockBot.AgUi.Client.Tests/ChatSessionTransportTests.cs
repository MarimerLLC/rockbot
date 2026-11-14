using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using RockBot.UserProxy;
using RockBot.UserProxy.ChatState;

namespace RockBot.AgUi.Client.Tests;

/// <summary>
/// The transport's own logic, against a scripted proxy: what it sends, how it reconnects the
/// notify stream, and what it does when the proxy refuses or the connection drops. No browser
/// and no server: the proxy here is an <see cref="HttpMessageHandler"/>.
/// </summary>
[TestClass]
public sealed class ChatSessionTransportTests
{
    private const string Thread = "agui-session";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>A proxy that answers from a script and records what it was asked.</summary>
    private sealed class ScriptedProxy : HttpMessageHandler
    {
        public sealed record Request(HttpMethod Method, string PathAndQuery, string? LastEventId, string? Authorization, string? Body);

        private readonly ConcurrentQueue<Request> _requests = new();

        /// <summary>What each notify connection gets, in order: an SSE body that then ends, or a status.</summary>
        public Queue<object> Notify { get; } = new();

        /// <summary>The body of <c>POST /agui</c>, or a status to refuse it with.</summary>
        public object Run { get; set; } = HttpStatusCode.ServiceUnavailable;

        public string History { get; set; } = """{"type":"MESSAGES_SNAPSHOT","messages":[]}""";

        /// <summary>What the history is from the second time it is asked for, when it has changed by then.</summary>
        public string? LaterHistory { get; set; }

        private int _historyCalls;

        public string Questions { get; set; } = """{"threadId":"agui-session","questions":[]}""";

        public HttpStatusCode AnswerStatus { get; set; } = HttpStatusCode.Accepted;

        public IReadOnlyList<Request> Requests => [.. _requests];

        public IReadOnlyList<Request> To(string path) => [.. _requests.Where(r => r.PathAndQuery.StartsWith(path, StringComparison.Ordinal))];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            _requests.Enqueue(new Request(
                request.Method, path,
                request.Headers.TryGetValues("Last-Event-ID", out var ids) ? ids.Single() : null,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (path.StartsWith("/agui/notify", StringComparison.Ordinal))
            {
                object next;
                lock (Notify)
                    next = Notify.Count > 0 ? Notify.Dequeue() : new HangingStream(cancellationToken);
                return Answer(next, "text/event-stream");
            }

            if (path == "/agui")
                return Answer(Run, "text/event-stream");
            if (path.EndsWith("/messages", StringComparison.Ordinal))
                return Answer(Interlocked.Increment(ref _historyCalls) > 1 && LaterHistory is { } later ? later : History, "application/json");
            if (path.EndsWith("/questions", StringComparison.Ordinal))
                return Answer(Questions, "application/json");
            if (path.StartsWith("/agent", StringComparison.Ordinal))
                return Answer("""{"agentName":"RockBot","agentVersion":"1.4.0","threadId":"agui-session","isProcessing":false,"work":[]}""", "application/json");
            if (path.StartsWith("/tickets", StringComparison.Ordinal))
                return Answer("""{"url":"/t/v1.abc.def/attachments/chart.png","ticket":"v1.abc.def","expiresAt":"2999-01-01T00:00:00+00:00"}""", "application/json");
            if (path.Contains("/answer", StringComparison.Ordinal))
                return Answer(AnswerStatus, "application/json");
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }

        private static HttpResponseMessage Answer(object script, string contentType) => script switch
        {
            HttpStatusCode status => new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    $$"""{"title":"Refused","detail":"The proxy said {{(int)status}}.","status":{{(int)status}}}""",
                    Encoding.UTF8, "application/problem+json"),
            },
            Stream stream => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) },
            string body => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Recorded.Normalize(body), Encoding.UTF8, contentType),
            },
            _ => throw new InvalidOperationException(),
        };

        /// <summary>A connection that stays open and silent until the client closes it.</summary>
        private sealed class HangingStream(CancellationToken closed) : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(closed, cancellationToken);
                await Task.Delay(Timeout.Infinite, both.Token);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private static (ChatSession Session, ChatStateService Chat) Open(ScriptedProxy proxy, Func<CancellationToken, ValueTask<string?>>? token = null)
    {
        var client = new AgUiProxyClient(new HttpClient(proxy), new AgUiProxyClientOptions
        {
            BaseAddress = new Uri("http://proxy.test/"), AccessToken = token,
        });
        var chat = new ChatStateService();
        var session = new ChatSession(client, chat, new ChatSessionOptions { ReconnectDelays = [TimeSpan.FromMilliseconds(5)] });
        return (session, chat);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private const string Connected = """
        id: e1-0
        data: {"type":"CUSTOM","name":"rockbot.connected","value":{"threadId":"agui-session"}}


        """;

    // ── The notify stream ────────────────────────────────────────────────────

    [TestMethod]
    public async Task Start_LoadsTheAgentTheHistoryAndTheOpenQuestions_AfterTheStreamSaysItIsConnected()
    {
        var proxy = new ScriptedProxy { History = Recorded.History, Questions = $$"""{"questions":[{{Recorded.QuestionValue}}]}""" };
        proxy.Notify.Enqueue(Connected);
        var (session, chat) = Open(proxy);
        await using var _ = session;

        session.Start();
        await UntilAsync(() => chat.State.FindQuestion("q-bde073c5") is not null, "the open question");

        Assert.AreEqual("RockBot", chat.State.AgentName);
        Assert.AreEqual("1.4.0", chat.State.AgentVersion);
        Assert.AreEqual("Here is an image.", chat.State.Messages.Single(m => m.Category == MessageCategory.PrimaryFinal).Content);

        // The order the contract asks for: the stream first, then the loads.
        var order = proxy.Requests.Select(r => r.PathAndQuery).ToList();
        Assert.AreEqual("/agui/notify?threadId=agui-session", order[0]);
        Assert.IsTrue(order.IndexOf("/threads/agui-session/messages") > 0);
        Assert.IsNull(proxy.Requests[0].LastEventId, "a first connection has no id to resume from");

        // The attachment in the history is shown from the url the proxy gave it.
        var image = chat.State.Messages.Single(m => m.Attachments is not null).Attachments!.Single();
        Assert.AreEqual("http://proxy.test/attachments/demo-fd34.png", (await session.AttachmentUrlAsync(image)).ToString());
    }

    [TestMethod]
    public async Task ADroppedStream_IsReconnectedWithTheLastEventId_AndWhatWasMissedIsApplied()
    {
        var proxy = new ScriptedProxy();
        // Connection 1: connected, one event, then the connection ends in the middle of a message.
        proxy.Notify.Enqueue(Connected + """
            id: e1-1
            data: {"type":"CUSTOM","name":"rockbot.error","value":{"message":"First."}}

            id: e1-2
            data: {"type":"TEXT_MESSAGE_START","messageId":"m1","role":"assistant","name":"RockBot"}


            """);
        // Connection 2: the proxy replays from e1-2, so the message carries on where it stopped.
        proxy.Notify.Enqueue("""
            id: e1-3
            data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"m1","delta":"Across a reconnect."}

            id: e1-4
            data: {"type":"TEXT_MESSAGE_END","messageId":"m1"}

            id: e1-5
            data: {"type":"CUSTOM","name":"rockbot.origin","value":{"messageId":"m1","source":{"kind":"primary"}}}


            """);
        var (session, chat) = Open(proxy);
        await using var _ = session;

        session.Start();
        await UntilAsync(() => chat.State.Messages.Any(m => m.Content == "Across a reconnect."), "the message split over two connections");
        await UntilAsync(() => proxy.To("/agui/notify").Count >= 3, "the third connection");

        var connections = proxy.To("/agui/notify");
        Assert.IsNull(connections[0].LastEventId);
        Assert.AreEqual("e1-2", connections[1].LastEventId);
        Assert.AreEqual("e1-5", connections[2].LastEventId);
        Assert.AreEqual("e1-5", session.LastEventId);

        Assert.AreEqual(1, proxy.To("/threads/agui-session/messages").Count, "a replayed reconnect loads nothing again");
        Assert.AreEqual(1, chat.State.Messages.Count(m => m.Content == "First."), "nothing is applied twice");
    }

    [TestMethod]
    public async Task Resync_RefetchesTheConversation_ClearsProgress_AndCarriesOnFromItsId()
    {
        // The conversation as the client has to fetch it again after the gap.
        var proxy = new ScriptedProxy { LaterHistory = Recorded.History };
        // A subagent is shown as working when the connection drops.
        proxy.Notify.Enqueue(Connected + """
            id: e1-1
            data: {"type":"ACTIVITY_SNAPSHOT","subagentRunId":"t1","messageId":"notify-progress-t1","activityType":"rockbot.progress","content":{"agentName":"subagent-t1","text":"Working"},"replace":true}


            """);
        // The proxy restarted: the id is unknown to it, so it can't replay.
        proxy.Notify.Enqueue("""
            id: e2-7
            data: {"type":"CUSTOM","name":"rockbot.resync","value":{"reason":"unknown"}}

            id: e2-8
            data: {"type":"CUSTOM","name":"rockbot.error","value":{"message":"After the resync."}}


            """);
        var (session, chat) = Open(proxy);
        await using var _ = session;

        var sawProgress = false;
        chat.OnStateChanged += () => sawProgress |= chat.State.HasOpenActivityLog;

        session.Start();
        await UntilAsync(() => chat.State.Messages.Any(m => m.Content == "After the resync."), "the event after the resync");

        Assert.AreEqual(2, proxy.To("/threads/agui-session/messages").Count, "once when connected, once for the resync");
        Assert.AreEqual(2, proxy.To("/threads/agui-session/questions").Count);
        Assert.IsTrue(chat.State.Messages.Any(m => m.Content == "Here is an image."), "the refetched history is shown");
        Assert.IsTrue(sawProgress, "the subagent was shown as working before the gap");
        Assert.IsFalse(chat.State.HasOpenActivityLog, "progress from before the gap is cleared: its end may have been missed");

        await UntilAsync(() => proxy.To("/agui/notify").Count >= 3, "the next connection");
        Assert.AreEqual("e1-1", proxy.To("/agui/notify")[1].LastEventId);
        Assert.AreEqual("e2-8", proxy.To("/agui/notify")[2].LastEventId, "it carries on from the new sequence");
    }

    [TestMethod]
    public async Task AProxyThatIsDown_IsRetried_AndTheConnectionStateSaysSo()
    {
        var proxy = new ScriptedProxy();
        proxy.Notify.Enqueue(HttpStatusCode.ServiceUnavailable);
        proxy.Notify.Enqueue(HttpStatusCode.Unauthorized);
        proxy.Notify.Enqueue(Connected);
        var (session, chat) = Open(proxy);
        await using var _ = session;
        var states = new ConcurrentQueue<(NotifyConnection, string?)>();
        session.ConnectionChanged += () => states.Enqueue((session.Connection, session.ConnectionError));

        session.Start();
        await UntilAsync(() => chat.State.AgentName == "RockBot", "the load after the third attempt connected");
        await UntilAsync(() => proxy.To("/agui/notify").Count >= 4, "the reconnect after that stream ended");

        Assert.IsTrue(states.Contains((NotifyConnection.Reconnecting, "The proxy said 503.")));
        Assert.IsTrue(states.Contains((NotifyConnection.Reconnecting, "The proxy said 401.")));
        Assert.IsTrue(states.Any(s => s.Item1 == NotifyConnection.Connected));
        Assert.AreEqual("e1-0", proxy.To("/agui/notify")[3].LastEventId);
    }

    [TestMethod]
    public async Task AThreadTheProxyRefuses_StopsTheStream_InsteadOfRetryingForEver()
    {
        var proxy = new ScriptedProxy();
        proxy.Notify.Enqueue(HttpStatusCode.Forbidden);
        var (session, _) = Open(proxy);
        await using var disposing = session;

        session.Start();
        await UntilAsync(() => session.ConnectionError is not null, "the refusal");
        await Task.Delay(100);

        Assert.AreEqual(NotifyConnection.Stopped, session.Connection);
        Assert.AreEqual("The proxy said 403.", session.ConnectionError);
        Assert.AreEqual(1, proxy.To("/agui/notify").Count);
    }

    // ── A run ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Send_PostsOneUserMessageWithTheClientsModeAndCapabilities_AndAppliesTheRun()
    {
        var proxy = new ScriptedProxy { Run = Recorded.RunWithToolCallAndTaskList };
        var (session, chat) = Open(proxy);
        await using var _ = session;
        var attachment = new AgentAttachment { Mime = "image/png", Path = "shot-1f3a.png", FileName = "shot.png" };

        await session.SendAsync("Hello", [attachment]);

        var request = proxy.To("/agui").Single();
        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.AreEqual(Thread, root.GetProperty("threadId").GetString());
        StringAssert.StartsWith(root.GetProperty("runId").GetString(), "run-");
        var message = root.GetProperty("messages").EnumerateArray().Single();
        Assert.AreEqual(("user", "Hello"), (message.GetProperty("role").GetString(), message.GetProperty("content").GetString()));

        var rockbot = root.GetProperty("forwardedProps").GetProperty("rockbot");
        Assert.AreEqual("custom", rockbot.GetProperty("questionMode").GetString());
        var capabilities = rockbot.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).ToList();
        CollectionAssert.Contains(capabilities, nameof(ClientCapabilities.MarkdownBasic));
        Assert.IsFalse(capabilities.Contains(nameof(ClientCapabilities.HtmlInline)), "the sample can't render agent HTML safely, so it doesn't ask for it");
        Assert.IsFalse(capabilities.Contains(nameof(ClientCapabilities.SvgInline)));
        Assert.IsTrue(capabilities.All(c => Enum.TryParse<ClientCapabilities>(c, out var _)), "every name is one the proxy knows");
        Assert.AreEqual(
            """[{"mime":"image/png","path":"shot-1f3a.png","fileName":"shot.png"}]""",
            rockbot.GetProperty("attachments").GetRawText());

        Assert.IsFalse(chat.IsBusy);
        Assert.AreEqual("Hello", chat.State.Messages[0].Content);
        Assert.AreEqual("shot-1f3a.png", chat.State.Messages[0].Attachments!.Single().Path);
        Assert.AreEqual("Echo: Hello", chat.State.Messages[^1].Content);
    }

    [TestMethod]
    public async Task ARunTheProxyRefuses_EndsTheTurnWithItsReason()
    {
        var proxy = new ScriptedProxy { Run = HttpStatusCode.Forbidden };
        var (session, chat) = Open(proxy);
        await using var _ = session;

        await session.SendAsync("Hello");

        Assert.IsFalse(chat.IsBusy);
        Assert.AreEqual("Error: The proxy said 403.", chat.State.Messages[^1].Content);
    }

    [TestMethod]
    public async Task ARunWhoseConnectionDrops_EndsTheTurn_SoTheUserCanSendAgain()
    {
        var proxy = new ScriptedProxy
        {
            Run = """
                data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"x","protocolVersion":"1.0"}

                data: {"type":"ACTIVITY_SNAPSHOT","messageId":"x-progress","activityType":"rockbot.progress","content":{"agentName":"RockBot","text":"Thinking…"},"replace":true}


                """,
        };
        var (session, chat) = Open(proxy);
        await using var _ = session;

        await session.SendAsync("Hello");

        Assert.IsFalse(chat.IsBusy);
        Assert.IsTrue(chat.State.Messages[^1].IsError);
        StringAssert.Contains(chat.State.Messages[^1].Content, "connection to the proxy was lost");
    }

    // ── Questions ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Answer_PostsTheFieldValues_AndAFailedPostOpensTheFormAgain()
    {
        var proxy = new ScriptedProxy { Questions = $$"""{"questions":[{{Recorded.QuestionValue}}]}""" };
        proxy.Notify.Enqueue(Connected);
        var (session, chat) = Open(proxy);
        await using var _ = session;
        session.Start();
        await UntilAsync(() => chat.State.FindQuestion("q-bde073c5") is not null, "the open question");

        proxy.AnswerStatus = HttpStatusCode.ServiceUnavailable;
        var answers = new System.Text.Json.Nodes.JsonObject { ["tz"] = "Europe/Brussels", ["notify"] = true };
        Assert.IsFalse(await session.AnswerAsync("q-bde073c5", answers));

        var form = chat.State.FindQuestion("q-bde073c5")!;
        Assert.IsFalse(form.IsSubmitting);
        Assert.AreEqual(McpQuestionState.Rejected, form.QuestionState, "rejected leaves the form open to submit again");
        Assert.AreEqual("The proxy said 503.", form.QuestionReason);

        proxy.AnswerStatus = HttpStatusCode.Accepted;
        Assert.IsTrue(await session.AnswerAsync("q-bde073c5", answers));
        Assert.IsTrue(chat.State.FindQuestion("q-bde073c5")!.IsSubmitting);

        var posted = proxy.To("/questions/q-bde073c5/answer");
        Assert.AreEqual(2, posted.Count);
        Assert.AreEqual("""{"threadId":"agui-session","answers":{"tz":"Europe/Brussels","notify":true}}""", posted[1].Body);

        // Already being sent: a second click doesn't post again.
        Assert.IsFalse(await session.AnswerAsync("q-bde073c5", answers));
        Assert.AreEqual(2, proxy.To("/questions/q-bde073c5/answer").Count);
    }

    [TestMethod]
    public async Task Decline_PostsADecline()
    {
        var proxy = new ScriptedProxy { Questions = $$"""{"questions":[{{Recorded.QuestionValue}}]}""" };
        proxy.Notify.Enqueue(Connected);
        var (session, chat) = Open(proxy);
        await using var _ = session;
        session.Start();
        await UntilAsync(() => chat.State.FindQuestion("q-bde073c5") is not null, "the open question");

        Assert.IsTrue(await session.AnswerAsync("q-bde073c5", answers: null, decline: true));

        Assert.AreEqual("""{"threadId":"agui-session","decline":true}""", proxy.To("/questions/").Single().Body);
    }

    // ── App mode ─────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task WithAToken_EveryRequestCarriesIt_AndAnAttachmentIsShownThroughATicket()
    {
        var proxy = new ScriptedProxy { Run = Recorded.RunWithToolCallAndTaskList };
        proxy.Notify.Enqueue(Connected);
        var tokens = 0;
        var (session, chat) = Open(proxy, _ => ValueTask.FromResult<string?>($"token-{Interlocked.Increment(ref tokens)}"));
        await using var _ = session;

        session.Start();
        await UntilAsync(() => chat.State.AgentName == "RockBot", "the first load");
        await session.SendAsync("Hello");

        Assert.IsTrue(proxy.Requests.Count >= 5);
        Assert.IsTrue(proxy.Requests.All(r => r.Authorization?.StartsWith("Bearer token-", StringComparison.Ordinal) == true),
            "the token is asked for again for each request, so a refreshed one is picked up");

        // <img src> can't send the header: the address comes from POST /tickets, and is reused
        // while the ticket is good.
        var image = new AgentAttachment { Mime = "image/png", Path = "chart.png" };
        var first = await session.AttachmentUrlAsync(image);
        var second = await session.AttachmentUrlAsync(image);

        Assert.AreEqual("http://proxy.test/t/v1.abc.def/attachments/chart.png", first.ToString());
        Assert.AreEqual(first, second);
        var ticket = proxy.To("/tickets").Single();
        Assert.AreEqual("""{"kind":"attachment","path":"chart.png"}""", ticket.Body);
    }

    [TestMethod]
    public void WithoutAUrlFromTheProxy_AnAttachmentsAddressIsItsEscapedPath()
    {
        Assert.AreEqual("/attachments/previews/ab%2012/deck.html", ChatSession.DefaultAttachmentUrl("previews/ab 12/deck.html"));
    }
}
