using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class UserProxyServiceTests
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
        _options = new UserProxyOptions { ProxyId = "test-proxy" };
        _service = new UserProxyService(
            _publisher,
            _subscriber,
            _frontend,
            _options,
            NullLogger<UserProxyService>.Instance);

        await _service.StartAsync(CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _service.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public void StartAsync_SubscribesToUserResponseTopic()
    {
        Assert.AreEqual(UserProxyTopics.UserResponse, _subscriber.CapturedTopic);
        Assert.AreEqual("user-proxy.test-proxy", _subscriber.CapturedSubscriptionName);
    }

    [TestMethod]
    public async Task SendAsync_PublishesToCorrectTopic()
    {
        var message = CreateUserMessage();

        // Start send but don't await — it will timeout since no reply comes
        var sendTask = _service.SendAsync(message, timeout: TimeSpan.FromMilliseconds(50));

        // Verify publish happened
        await Task.Delay(10);
        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual(UserProxyTopics.UserMessage, _publisher.Published[0].Topic);

        // Let it timeout
        var result = await sendTask;
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SendAsync_SetsCorrelationIdAndReplyTo()
    {
        var message = CreateUserMessage();

        var sendTask = _service.SendAsync(message, timeout: TimeSpan.FromMilliseconds(50));
        await Task.Delay(10);

        var envelope = _publisher.Published[0].Envelope;
        Assert.IsNotNull(envelope.CorrelationId);
        Assert.AreEqual(UserProxyTopics.UserResponse, envelope.ReplyTo);
        Assert.AreEqual("test-proxy", envelope.Source);

        await sendTask;
    }

    [TestMethod]
    public async Task SendAsync_SetsDestinationFromTargetAgent()
    {
        var message = new UserMessage
        {
            Content = "Hello",
            SessionId = "s1",
            UserId = "u1",
            TargetAgent = "agent-alpha"
        };

        var sendTask = _service.SendAsync(message, timeout: TimeSpan.FromMilliseconds(50));
        await Task.Delay(10);

        var envelope = _publisher.Published[0].Envelope;
        Assert.AreEqual("agent-alpha", envelope.Destination);

        await sendTask;
    }

    [TestMethod]
    public async Task SendAsync_ReturnsReply_OnCorrelationMatch()
    {
        var message = CreateUserMessage();

        var sendTask = _service.SendAsync(message, timeout: TimeSpan.FromSeconds(5));
        await Task.Delay(10);

        // Simulate a reply with matching correlation
        var correlationId = _publisher.Published[0].Envelope.CorrelationId!;
        var reply = new AgentReply
        {
            Content = "Hi there",
            SessionId = "s1",
            AgentName = "test-agent"
        };
        var replyEnvelope = TestEnvelopeHelper.CreateEnvelope(reply,
            source: "test-agent",
            correlationId: correlationId);

        await _subscriber.CapturedHandler!(replyEnvelope, CancellationToken.None);

        var result = await sendTask;
        Assert.IsNotNull(result);
        Assert.AreEqual("Hi there", result.Content);
        Assert.AreEqual("test-agent", result.AgentName);
    }

    [TestMethod]
    public async Task SendAsync_ReturnsNull_OnTimeout()
    {
        var message = CreateUserMessage();

        var result = await _service.SendAsync(message, timeout: TimeSpan.FromMilliseconds(50));

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task HandleResponse_DeadLetters_InvalidPayload()
    {
        var badEnvelope = MessageEnvelope.Create(
            messageType: "bad",
            body: new byte[] { 0xFF, 0xFE },
            source: "bad-source");

        var result = await _subscriber.CapturedHandler!(badEnvelope, CancellationToken.None);
        Assert.AreEqual(MessageResult.DeadLetter, result);
    }

    [TestMethod]
    public async Task HandleResponse_DeadLetters_EmptyContent()
    {
        var reply = new AgentReply
        {
            Content = "",
            SessionId = "s1",
            AgentName = "agent"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(reply, source: "agent");

        var result = await _subscriber.CapturedHandler!(envelope, CancellationToken.None);
        Assert.AreEqual(MessageResult.DeadLetter, result);
    }

    [TestMethod]
    public async Task HandleResponse_DisplaysUnsolicitedReply_ViaFrontend()
    {
        var reply = new AgentReply
        {
            Content = "Unsolicited hello",
            SessionId = "s1",
            AgentName = "agent-x"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(reply,
            source: "agent-x",
            correlationId: "no-match");

        var result = await _subscriber.CapturedHandler!(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.AreEqual(1, _frontend.DisplayedReplies.Count);
        Assert.AreEqual("Unsolicited hello", _frontend.DisplayedReplies[0].Content);
    }

    [TestMethod]
    public async Task SendFireAndForgetAsync_PublishesWithoutWaiting()
    {
        var message = CreateUserMessage();

        await _service.SendFireAndForgetAsync(message);

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual(UserProxyTopics.UserMessage, _publisher.Published[0].Topic);
        Assert.IsNotNull(_publisher.Published[0].Envelope.CorrelationId);
    }

    [TestMethod]
    public async Task StopAsync_CancelsPendingRequests()
    {
        var message = CreateUserMessage();

        var sendTask = _service.SendAsync(message, timeout: TimeSpan.FromSeconds(30));
        await Task.Delay(10);

        await _service.StopAsync(CancellationToken.None);

        // The send task should complete (return null or throw canceled)
        // Since TCS was canceled, WaitAsync will throw OperationCanceledException
        // which is caught by the timeout handler — but the token IS the external one,
        // so it re-throws. The finally block removes from pending.
        // Actually: TCS.TrySetCanceled makes the task throw OperationCanceledException
        // which bubbles through WaitAsync.
        try
        {
            await sendTask;
        }
        catch (OperationCanceledException)
        {
            // Expected — the pending TCS was canceled by StopAsync
        }
    }

    private static UserMessage CreateUserMessage(string content = "Hello") =>
        new()
        {
            Content = content,
            SessionId = "session-1",
            UserId = "user-1"
        };

    // ── GetHistoryAsync tests ────────────────────────────────────────────────

    [TestMethod]
    public async Task GetHistoryAsync_PublishesToConversationHistoryRequestTopic()
    {
        var historyTask = _service.GetHistoryAsync("session-1", timeout: TimeSpan.FromMilliseconds(50));
        await Task.Delay(10);

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual(UserProxyTopics.ConversationHistoryRequest, _publisher.Published[0].Topic);

        await historyTask; // let it timeout
    }

    [TestMethod]
    public async Task GetHistoryAsync_SetsCorrectCorrelationIdAndReplyTo()
    {
        var historyTask = _service.GetHistoryAsync("session-1", timeout: TimeSpan.FromMilliseconds(50));
        await Task.Delay(10);

        var envelope = _publisher.Published[0].Envelope;
        Assert.IsNotNull(envelope.CorrelationId);
        Assert.AreEqual($"{UserProxyTopics.ConversationHistoryResponse}.{_options.ProxyId}", envelope.ReplyTo);
        Assert.AreEqual("test-proxy", envelope.Source);

        await historyTask;
    }

    [TestMethod]
    public async Task GetHistoryAsync_ReturnsNull_OnTimeout()
    {
        var result = await _service.GetHistoryAsync("session-1", timeout: TimeSpan.FromMilliseconds(50));
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task GetHistoryAsync_ReturnsHistory_OnCorrelatedResponse()
    {
        var historyTask = _service.GetHistoryAsync("session-1", timeout: TimeSpan.FromSeconds(5));
        await Task.Delay(10);

        var correlationId = _publisher.Published[0].Envelope.CorrelationId!;
        var expectedTopic = $"{UserProxyTopics.ConversationHistoryResponse}.{_options.ProxyId}";

        var response = new ConversationHistoryResponse
        {
            Turns =
            [
                new ConversationHistoryTurn { Role = "user", Content = "Hello", Timestamp = DateTimeOffset.UtcNow },
                new ConversationHistoryTurn { Role = "assistant", Content = "Hi!", Timestamp = DateTimeOffset.UtcNow }
            ]
        };

        var responseEnvelope = TestEnvelopeHelper.CreateEnvelope(response,
            source: "RockBot",
            correlationId: correlationId);

        var historyHandler = _subscriber.GetHandlerForTopic(expectedTopic);
        Assert.IsNotNull(historyHandler, "History response handler should be subscribed");

        await historyHandler(responseEnvelope, CancellationToken.None);

        var result = await historyTask;
        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Turns.Count);
        Assert.AreEqual("Hello", result.Turns[0].Content);
        Assert.AreEqual("user", result.Turns[0].Role);
        Assert.AreEqual("Hi!", result.Turns[1].Content);
        Assert.AreEqual("assistant", result.Turns[1].Role);
    }

    [TestMethod]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenNoHistory()
    {
        var historyTask = _service.GetHistoryAsync("session-empty", timeout: TimeSpan.FromSeconds(5));
        await Task.Delay(10);

        var correlationId = _publisher.Published[0].Envelope.CorrelationId!;
        var expectedTopic = $"{UserProxyTopics.ConversationHistoryResponse}.{_options.ProxyId}";

        var response = new ConversationHistoryResponse { Turns = [] };
        var responseEnvelope = TestEnvelopeHelper.CreateEnvelope(response,
            source: "RockBot",
            correlationId: correlationId);

        var historyHandler = _subscriber.GetHandlerForTopic(expectedTopic);
        Assert.IsNotNull(historyHandler);
        await historyHandler(responseEnvelope, CancellationToken.None);

        var result = await historyTask;
        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Turns.Count);
    }
}
