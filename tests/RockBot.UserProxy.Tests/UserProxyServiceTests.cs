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
        Assert.AreEqual($"{UserProxyTopics.UserResponse}.{_options.AgentName}", _subscriber.CapturedTopic);
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
        Assert.AreEqual($"{UserProxyTopics.UserMessage}.{_options.AgentName}", _publisher.Published[0].Topic);

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
        Assert.AreEqual($"{UserProxyTopics.UserResponse}.{_options.AgentName}", envelope.ReplyTo);
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
            AgentName = "agent-x",
            IsFinal = true
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
    public async Task HandleResponse_UnsolicitedNonFinal_RoutesToDisplayStatus()
    {
        // Unsolicited progress (subagent / A2A status) — IsFinal=false should land
        // in DisplayStatusAsync, not DisplayReplyAsync, so it doesn't stack as a bubble.
        var reply = new AgentReply
        {
            Content = "Searching the web...",
            SessionId = "s1",
            AgentName = "subagent-research",
            IsFinal = false
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(reply,
            source: "subagent-research",
            correlationId: "no-match");

        var result = await _subscriber.CapturedHandler!(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.AreEqual(0, _frontend.DisplayedReplies.Count,
            "Non-final unsolicited reply must not be displayed as a chat bubble");
        Assert.AreEqual(1, _frontend.DisplayedStatusReplies.Count);
        Assert.AreEqual("Searching the web...", _frontend.DisplayedStatusReplies[0].Content);
    }

    [TestMethod]
    public async Task HandleResponse_UnsolicitedFinal_RoutesToDisplayReply()
    {
        var reply = new AgentReply
        {
            Content = "Final answer",
            SessionId = "s1",
            AgentName = "agent-x",
            IsFinal = true
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(reply,
            source: "agent-x",
            correlationId: "no-match");

        var result = await _subscriber.CapturedHandler!(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        Assert.AreEqual(1, _frontend.DisplayedReplies.Count);
        Assert.AreEqual(0, _frontend.DisplayedStatusReplies.Count,
            "Final unsolicited reply must not be routed to DisplayStatusAsync");
    }

    [TestMethod]
    public async Task SendFireAndForgetAsync_PublishesWithoutWaiting()
    {
        var message = CreateUserMessage();

        await _service.SendFireAndForgetAsync(message);

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual($"{UserProxyTopics.UserMessage}.{_options.AgentName}", _publisher.Published[0].Topic);
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

    // ── Retry tests ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task StartAsync_RetriesAndConnects_WhenInitialSubscribeFails()
    {
        var failingSubscriber = new FailingThenSucceedingSubscriber(failCount: 2);
        var retryOptions = new UserProxyOptions
        {
            ProxyId = "retry-proxy",
            MaxSubscribeRetries = 5,
            SubscribeRetryBaseDelay = TimeSpan.FromMilliseconds(10),
            MaxSubscribeRetryDelay = TimeSpan.FromMilliseconds(50)
        };

        var svc = new UserProxyService(
            new TrackingPublisher(),
            failingSubscriber,
            new StubUserFrontend(),
            retryOptions,
            NullLogger<UserProxyService>.Instance);

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnConnectionChanged += () =>
        {
            if (svc.IsConnected) connected.TrySetResult();
        };

        await svc.StartAsync(CancellationToken.None);

        // Initial attempt fails — not connected yet
        Assert.IsFalse(svc.IsConnected);

        // Wait for background retry to succeed
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(svc.IsConnected);
        Assert.AreEqual($"{UserProxyTopics.UserResponse}.{retryOptions.AgentName}", failingSubscriber.CapturedTopic);
        // 1 initial failure + 1 retry failure + 1 retry success = 3 total calls
        Assert.AreEqual(3, failingSubscriber.CallCount);

        await svc.StopAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task StartAsync_RetryStops_WhenServiceIsStopped()
    {
        // Subscriber that always fails
        var alwaysFailSubscriber = new FailingThenSucceedingSubscriber(failCount: int.MaxValue);
        var retryOptions = new UserProxyOptions
        {
            ProxyId = "stop-proxy",
            MaxSubscribeRetries = int.MaxValue,
            SubscribeRetryBaseDelay = TimeSpan.FromMilliseconds(10),
            MaxSubscribeRetryDelay = TimeSpan.FromMilliseconds(10)
        };

        var svc = new UserProxyService(
            new TrackingPublisher(),
            alwaysFailSubscriber,
            new StubUserFrontend(),
            retryOptions,
            NullLogger<UserProxyService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        Assert.IsFalse(svc.IsConnected);

        // Let a few retries happen
        await Task.Delay(100);

        // Stop the service — should cancel the retry loop
        await svc.StopAsync(CancellationToken.None);

        var countAtStop = alwaysFailSubscriber.CallCount;

        // Wait a bit more to verify no more retries are happening
        await Task.Delay(100);
        Assert.AreEqual(countAtStop, alwaysFailSubscriber.CallCount,
            "Retry loop should stop after StopAsync");
        Assert.IsFalse(svc.IsConnected);
    }

    [TestMethod]
    public async Task StartAsync_NoRetry_WhenMaxSubscribeRetriesIsZero()
    {
        var failingSubscriber = new FailingThenSucceedingSubscriber(failCount: 1);
        var retryOptions = new UserProxyOptions
        {
            ProxyId = "no-retry-proxy",
            MaxSubscribeRetries = 0,
            SubscribeRetryBaseDelay = TimeSpan.FromMilliseconds(10)
        };

        var svc = new UserProxyService(
            new TrackingPublisher(),
            failingSubscriber,
            new StubUserFrontend(),
            retryOptions,
            NullLogger<UserProxyService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        Assert.IsFalse(svc.IsConnected);

        // Wait to ensure no background retry is happening
        await Task.Delay(50);

        Assert.AreEqual(1, failingSubscriber.CallCount,
            "Should only have the initial attempt, no retries");
        Assert.IsFalse(svc.IsConnected);

        await svc.StopAsync(CancellationToken.None);
    }

    // ── GetHistoryAsync tests ────────────────────────────────────────────────

    [TestMethod]
    public async Task GetHistoryAsync_PublishesToConversationHistoryRequestTopic()
    {
        var historyTask = _service.GetHistoryAsync("session-1", timeout: TimeSpan.FromMilliseconds(50));
        await Task.Delay(10);

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual($"{UserProxyTopics.ConversationHistoryRequest}.{_options.AgentName}", _publisher.Published[0].Topic);

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
