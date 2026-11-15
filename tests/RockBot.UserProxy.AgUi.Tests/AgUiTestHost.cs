using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RockBot.Messaging;
using RockBot.Messaging.InProcess;

namespace RockBot.UserProxy.AgUi.Tests;

/// <summary>
/// The AG-UI proxy over the real HTTP pipeline, with the in-process bus in place of RabbitMQ. The
/// proxy's own <see cref="UserProxyService"/> runs, so a test's fake agent talks to it over the
/// same topics a real agent would.
/// </summary>
internal sealed class AgUiTestHost(params (string Key, string Value)[] settings) : WebApplicationFactory<Program>
{
    public const string AgentName = "RockBot";

    /// <summary>Extra service registrations, applied last (a clock a test controls, say).</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("UserProxy:ProxyId", "agui-test");
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            // Everything RabbitMQ: the publisher and subscriber, and the DLQ reporter that would
            // dial the broker's management API.
            var rabbit = services
                .Where(d => d.ServiceType == typeof(IMessagePublisher)
                    || d.ServiceType == typeof(IMessageSubscriber)
                    || (d.ServiceType == typeof(IHostedService)
                        && d.ImplementationType?.Namespace?.StartsWith("RockBot.Messaging.RabbitMQ", StringComparison.Ordinal) == true))
                .ToList();
            foreach (var descriptor in rabbit)
                services.Remove(descriptor);

            services.AddRockBotInProcessMessaging();
            ConfigureServices?.Invoke(services);
        });
    }

    public IMessagePublisher Publisher => Services.GetRequiredService<IMessagePublisher>();

    public IMessageSubscriber Subscriber => Services.GetRequiredService<IMessageSubscriber>();
}

/// <summary>
/// Stands in for the agent on the bus. Records what it receives; a test decides what it sends
/// back by setting <see cref="OnMessage"/>.
/// </summary>
internal sealed class FakeAgent : IAsyncDisposable
{
    private readonly IMessagePublisher _publisher;
    private readonly List<ISubscription> _subscriptions = [];
    private readonly Lock _lock = new();
    private readonly List<ReceivedMessage> _messages = [];
    private readonly TaskCompletionSource<CancelSessionRequest> _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<McpQuestionAnswer> _answers = [];

    private FakeAgent(IMessagePublisher publisher) => _publisher = publisher;

    public sealed record ReceivedMessage(MessageEnvelope Envelope, UserMessage Message);

    /// <summary>What the agent does with a user message. The default answers "Echo: …".</summary>
    public Func<FakeAgent, ReceivedMessage, Task> OnMessage { get; set; } =
        (agent, received) => agent.ReplyAsync(received, $"Echo: {received.Message.Content}");

    /// <summary>What the agent does with a cancel request, after recording it.</summary>
    public Func<FakeAgent, CancelSessionRequest, Task>? OnCancel { get; set; }

    /// <summary>What the agent does with a form answer, after recording it. The default does nothing.</summary>
    public Func<FakeAgent, McpQuestionAnswer, Task>? OnAnswer { get; set; }

    /// <summary>The form answers the agent received, in order.</summary>
    public IReadOnlyList<McpQuestionAnswer> Answers
    {
        get { lock (_lock) return [.. _answers]; }
    }

    /// <summary>The history the agent returns, or null to leave history requests unanswered.</summary>
    public ConversationHistoryResponse? History { get; set; }

    /// <summary>The last history request the agent was sent.</summary>
    public ConversationHistoryRequest? LastHistoryRequest { get; private set; }

    // ── The REST extras (phase 4). Each answer is null by default: the agent stays silent. ──

    /// <summary>What the agent answers an upload with. The bytes it was sent are in <see cref="Uploads"/>.</summary>
    public Func<AttachmentUploadRequest, AttachmentUploadResponse?>? OnUpload { get; set; }

    public AgentInfoResponse? Info { get; set; }

    public Func<ActiveStatusRequest, ActiveStatusResponse?>? OnStatus { get; set; }

    public ListSavedResponsesResponse? Saved { get; set; }

    public Func<SaveResponseRequest, SaveResponseAck?>? OnSave { get; set; }

    public Func<string, GetSavedResponseResponse?>? OnGetSaved { get; set; }

    public Func<string, DeleteSavedResponseAck?>? OnDeleteSaved { get; set; }

    public McpSignInListResponse? SignIns { get; set; }

    public Func<McpSignInStartRequest, McpSignInStartResponse?>? OnSignInStart { get; set; }

    /// <summary>What the agent does with a clear-context request, after recording it.</summary>
    public Func<FakeAgent, ClearContextRequest, Task>? OnClear { get; set; }

    private readonly List<object> _received = [];

    /// <summary>Every request of type <typeparamref name="T"/> the agent received on the phase 4 topics, in order.</summary>
    public IReadOnlyList<T> Received<T>()
    {
        lock (_lock) return [.. _received.OfType<T>()];
    }

    public IReadOnlyList<AttachmentUploadRequest> Uploads => Received<AttachmentUploadRequest>();

    /// <summary>Waits until the agent has received a <typeparamref name="T"/>, and returns the first.</summary>
    public async Task<T> ReceivedAsync<T>()
    {
        var deadline = DateTime.UtcNow + NotifyStream.Patience;
        while (true)
        {
            if (Received<T>() is [var first, ..])
                return first;
            Assert.IsTrue(DateTime.UtcNow < deadline, $"the agent never received a {typeof(T).Name}");
            await Task.Delay(10);
        }
    }

    private void Record(object request)
    {
        lock (_lock) _received.Add(request);
    }

    /// <summary>Subscribes to a request topic: records each request and publishes the answer, if any, to its reply topic.</summary>
    private async Task AnswerAsync<TRequest, TResponse>(
        IMessageSubscriber subscriber, string topic, Func<TRequest, TResponse?> answer)
        where TRequest : class where TResponse : class
    {
        _subscriptions.Add(await subscriber.SubscribeAsync(
            $"{topic}.{AgUiTestHost.AgentName}", $"fake-agent.{topic}",
            async (envelope, ct) =>
            {
                var request = envelope.GetPayload<TRequest>()!;
                Record(request);
                if (answer(request) is { } response && envelope.ReplyTo is { } replyTo)
                {
                    await _publisher.PublishAsync(replyTo,
                        response.ToEnvelope<TResponse>(source: AgUiTestHost.AgentName, correlationId: envelope.CorrelationId), ct);
                }
                return MessageResult.Ack;
            }));
    }

    private async Task SubscribeExtrasAsync(IMessageSubscriber subscriber)
    {
        await AnswerAsync<AttachmentUploadRequest, AttachmentUploadResponse>(
            subscriber, UserProxyTopics.AttachmentUploadRequest, r => OnUpload?.Invoke(r));
        await AnswerAsync<AgentInfoRequest, AgentInfoResponse>(subscriber, UserProxyTopics.AgentInfoRequest, _ => Info);
        await AnswerAsync<ActiveStatusRequest, ActiveStatusResponse>(
            subscriber, UserProxyTopics.ActiveStatusRequest, r => OnStatus?.Invoke(r));
        await AnswerAsync<ListSavedResponsesRequest, ListSavedResponsesResponse>(
            subscriber, UserProxyTopics.ListSavedResponsesRequest, _ => Saved);
        await AnswerAsync<SaveResponseRequest, SaveResponseAck>(
            subscriber, UserProxyTopics.SaveResponseRequest, r => OnSave?.Invoke(r));
        await AnswerAsync<GetSavedResponseRequest, GetSavedResponseResponse>(
            subscriber, UserProxyTopics.GetSavedResponseRequest, r => OnGetSaved?.Invoke(r.Id));
        await AnswerAsync<DeleteSavedResponseRequest, DeleteSavedResponseAck>(
            subscriber, UserProxyTopics.DeleteSavedResponseRequest, r => OnDeleteSaved?.Invoke(r.Id));

        // Fire-and-forget: nothing to answer.
        await AnswerAsync<UserFeedback, object>(subscriber, UserProxyTopics.UserFeedback, _ => null);

        _subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.ClearContext}.{AgUiTestHost.AgentName}", "fake-agent.clear",
            async (envelope, _) =>
            {
                var request = envelope.GetPayload<ClearContextRequest>()!;
                Record(request);
                if (OnClear is { } onClear)
                    await onClear(this, request);
                return MessageResult.Ack;
            }));

        // The bridge's sign-in topic carries three message types, told apart by the envelope's type.
        _subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.McpSignIn}.{AgUiTestHost.AgentName}", "fake-agent.mcp-signin",
            async (envelope, ct) =>
            {
                object? response = null;
                if (envelope.MessageType == typeof(McpSignInStartRequest).FullName)
                {
                    var start = envelope.GetPayload<McpSignInStartRequest>()!;
                    Record(start);
                    response = OnSignInStart?.Invoke(start);
                }
                else if (envelope.MessageType == typeof(McpSignInListRequest).FullName)
                {
                    Record(envelope.GetPayload<McpSignInListRequest>()!);
                    response = SignIns;
                }
                else if (envelope.MessageType == typeof(McpSignInCallback).FullName)
                {
                    Record(envelope.GetPayload<McpSignInCallback>()!);
                }

                if (envelope.ReplyTo is { } replyTo)
                {
                    if (response is McpSignInStartResponse started)
                        await _publisher.PublishAsync(replyTo, started.ToEnvelope<McpSignInStartResponse>(source: AgUiTestHost.AgentName, correlationId: envelope.CorrelationId), ct);
                    else if (response is McpSignInListResponse list)
                        await _publisher.PublishAsync(replyTo, list.ToEnvelope<McpSignInListResponse>(source: AgUiTestHost.AgentName, correlationId: envelope.CorrelationId), ct);
                }
                return MessageResult.Ack;
            }));
    }

    public IReadOnlyList<ReceivedMessage> Messages
    {
        get { lock (_lock) return [.. _messages]; }
    }

    /// <summary>Completes with the first cancel request the agent receives.</summary>
    public Task<CancelSessionRequest> CancelRequested => _cancel.Task;

    public static async Task<FakeAgent> StartAsync(AgUiTestHost host)
    {
        var agent = new FakeAgent(host.Publisher);
        var subscriber = host.Subscriber;

        agent._subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.UserMessage}.{AgUiTestHost.AgentName}", "fake-agent.messages",
            async (envelope, _) =>
            {
                var received = new ReceivedMessage(envelope, envelope.GetPayload<UserMessage>()!);
                lock (agent._lock) agent._messages.Add(received);
                await agent.OnMessage(agent, received);
                return MessageResult.Ack;
            }));

        agent._subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.CancelSession}.{AgUiTestHost.AgentName}", "fake-agent.cancel",
            async (envelope, _) =>
            {
                var request = envelope.GetPayload<CancelSessionRequest>()!;
                agent._cancel.TrySetResult(request);
                if (agent.OnCancel is { } onCancel)
                    await onCancel(agent, request);
                return MessageResult.Ack;
            }));

        agent._subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.McpAnswer}.{AgUiTestHost.AgentName}", "fake-agent.mcp-answer",
            async (envelope, _) =>
            {
                var answer = envelope.GetPayload<McpQuestionAnswer>()!;
                lock (agent._lock) agent._answers.Add(answer);
                if (agent.OnAnswer is { } onAnswer)
                    await onAnswer(agent, answer);
                return MessageResult.Ack;
            }));

        agent._subscriptions.Add(await subscriber.SubscribeAsync(
            $"{UserProxyTopics.ConversationHistoryRequest}.{AgUiTestHost.AgentName}", "fake-agent.history",
            async (envelope, ct) =>
            {
                agent.LastHistoryRequest = envelope.GetPayload<ConversationHistoryRequest>();
                if (agent.History is { } history && envelope.ReplyTo is { } replyTo)
                {
                    await agent._publisher.PublishAsync(replyTo,
                        history.ToEnvelope<ConversationHistoryResponse>(source: AgUiTestHost.AgentName, correlationId: envelope.CorrelationId),
                        ct);
                }
                return MessageResult.Ack;
            }));

        await agent.SubscribeExtrasAsync(subscriber);
        return agent;
    }

    /// <summary>Publishes <paramref name="payload"/> to the turn's reply topic, with its correlation id.</summary>
    public Task SendAsync<T>(ReceivedMessage to, T payload) =>
        _publisher.PublishAsync(to.Envelope.ReplyTo!,
            payload.ToEnvelope<T>(source: AgUiTestHost.AgentName, correlationId: to.Envelope.CorrelationId));

    /// <summary>
    /// Publishes <paramref name="payload"/> on the reply broadcast topic every proxy hears, with
    /// no correlation id: unsolicited replies, question and sign-in statuses, name changes.
    /// </summary>
    public Task BroadcastAsync<T>(T payload) =>
        _publisher.PublishAsync($"{UserProxyTopics.UserResponse}.{AgUiTestHost.AgentName}",
            payload.ToEnvelope<T>(source: AgUiTestHost.AgentName));

    /// <summary>Publishes <paramref name="payload"/> on the event broadcast topic: subagent lifecycle, their task lists.</summary>
    public Task EventAsync<T>(T payload) =>
        _publisher.PublishAsync($"{UserProxyTopics.UserEvent}.{AgUiTestHost.AgentName}",
            payload.ToEnvelope<T>(source: AgUiTestHost.AgentName));

    // MCP questions, as the bridge and McpQuestionAnswerHandler send them: all broadcast, none correlated.

    /// <summary>Shows a question as a form, as the bridge does: a final reply that carries the form and its text.</summary>
    public Task FormAsync(McpQuestionForm form, string sessionId = AgUiOptions.DefaultThreadId) => BroadcastAsync(new AgentReply
    {
        Content = $"{form.ServerName} asks: {form.Message}",
        SessionId = sessionId,
        AgentName = "mcp-bridge",
        IsFinal = true,
        Source = ReplySource.Bridge,
        ContentType = McpQuestionForm.ContentType,
        StructuredData = JsonSerializer.Serialize(form, TurnEventMapper.PayloadJson),
    });

    public Task StatusAsync(
        string questionId, string state, string? answeredBy = null, string? reason = null,
        string sessionId = AgUiOptions.DefaultThreadId) =>
        BroadcastAsync(new McpQuestionStatus
        {
            SessionId = sessionId, QuestionId = questionId, State = state, AnsweredBy = answeredBy, Reason = reason,
        });

    /// <summary>The agent's reply after an accepted form answer.</summary>
    public Task FollowUpAsync(string questionId, string content, string sessionId = AgUiOptions.DefaultThreadId) =>
        BroadcastAsync(new AgentReply
        {
            Content = content, SessionId = sessionId, AgentName = AgUiTestHost.AgentName, IsFinal = true,
            Source = ReplySource.Primary, FollowsQuestionId = questionId,
        });

    /// <summary>Accepts a form answer the way the real agent does: the bridge's status, then the reply.</summary>
    public async Task AcceptAsync(McpQuestionAnswer answer, string reply)
    {
        await StatusAsync(answer.QuestionId, answer.Decline ? McpQuestionState.Declined : McpQuestionState.Answered,
            answeredBy: "user", sessionId: answer.SessionId);
        await FollowUpAsync(answer.QuestionId, reply, answer.SessionId);
    }

    public Task ReplyAsync(ReceivedMessage to, string content, bool isFinal = true, ToolCallInfo? toolCall = null) =>
        SendAsync(to, new AgentReply
        {
            Content = content, SessionId = to.Message.SessionId, AgentName = AgUiTestHost.AgentName, IsFinal = isFinal, ToolCall = toolCall,
        });

    public Task RunStartedAsync(ReceivedMessage to) => SendAsync(to, new RunStarted
    {
        ThreadId = to.Message.SessionId, RunId = to.Envelope.CorrelationId!, AgentName = AgUiTestHost.AgentName,
        HeartbeatIntervalSeconds = 10, Timestamp = DateTimeOffset.UtcNow,
    });

    public Task RunFinishedAsync(ReceivedMessage to, string outcome, string? error = null) => SendAsync(to, new RunFinished
    {
        ThreadId = to.Message.SessionId, RunId = to.Envelope.CorrelationId!, Outcome = outcome, Error = error,
        Timestamp = DateTimeOffset.UtcNow,
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
            await subscription.DisposeAsync();
    }
}

/// <summary>
/// An open notify stream, read one server-sent event at a time. Comments (keepalives) are counted
/// and skipped.
/// </summary>
internal sealed class NotifyStream : IDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private NotifyStream(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    public sealed record Frame(string Id, JsonElement Event)
    {
        public string Type => Sse.Type(Event);

        /// <summary>A <c>CUSTOM</c> event's name.</summary>
        public string? Name => Sse.Str(Event, "name");

        /// <summary>A <c>CUSTOM</c> event's value.</summary>
        public JsonElement Value => Event.GetProperty("value");

        /// <summary>The id's sequence number: what follows the epoch.</summary>
        public long Sequence => long.Parse(Id[(Id.LastIndexOf('-') + 1)..]);
    }

    public HttpResponseMessage Response => _response;

    public int Comments { get; private set; }

    public static async Task<NotifyStream> OpenAsync(
        HttpClient client, string threadId = "agui-session", string? lastEventId = null, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/agui/notify?threadId={Uri.EscapeDataString(threadId)}{query}");
        if (lastEventId is not null)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(Patience);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        return new NotifyStream(response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    /// <summary>The next event, or null when the stream has ended.</summary>
    public async Task<Frame?> NextAsync()
    {
        string? id = null;
        string? data = null;
        while (await _reader.ReadLineAsync().WaitAsync(Patience) is { } line)
        {
            if (line.StartsWith(':'))
                Comments++;
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
                id = line["id: ".Length..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                data = line["data: ".Length..];
            else if (line.Length == 0 && data is not null)
                return new Frame(id ?? "", JsonDocument.Parse(data).RootElement.Clone());
        }
        return null;
    }

    /// <summary>The next event; fails if the stream ended instead.</summary>
    public async Task<Frame> ReadAsync() =>
        await NextAsync() ?? throw new AssertFailedException("the notify stream ended");

    public async Task<List<Frame>> ReadAsync(int count)
    {
        var frames = new List<Frame>(count);
        while (frames.Count < count)
            frames.Add(await ReadAsync());
        return frames;
    }

    /// <summary>Reads past the <c>rockbot.connected</c> event a new stream starts with, and returns it.</summary>
    public async Task<Frame> ReadConnectedAsync()
    {
        var frame = await ReadAsync();
        Assert.AreEqual(TurnEventMapper.CustomNames.Connected, frame.Name);
        return frame;
    }

    public void Dispose()
    {
        _reader.Dispose();
        _response.Dispose();
    }
}

/// <summary>Reads a run's server-sent events.</summary>
internal static class Sse
{
    /// <summary>The JSON of every <c>data:</c> event in <paramref name="body"/>, in order.</summary>
    public static List<JsonElement> Events(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.StartsWith("data: ", StringComparison.Ordinal))
            .Select(block => JsonDocument.Parse(block["data: ".Length..]).RootElement.Clone())
            .ToList();

    public static string Type(JsonElement e) => e.GetProperty("type").GetString()!;

    public static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.GetString() : null;
}
