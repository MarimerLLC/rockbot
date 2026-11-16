using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

/// <summary>
/// #841: the proxy attaches the other sessions' results its frontend showed to the next message
/// sent in a session, so the agent can import them as context.
/// </summary>
[TestClass]
public sealed class RelatedReplyTrackingTests
{
    private TrackingPublisher _publisher = null!;
    private StubSubscriber _subscriber = null!;
    private StubUserFrontend _frontend = null!;
    private UserProxyService _service = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _publisher = new TrackingPublisher();
        _subscriber = new StubSubscriber();
        _frontend = new StubUserFrontend();
        _service = new UserProxyService(
            _publisher, _subscriber, _frontend, new UserProxyOptions { ProxyId = "test-proxy" },
            NullLogger<UserProxyService>.Instance);
        await _service.StartAsync(CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup() => await _service.StopAsync(CancellationToken.None);

    [TestMethod]
    public async Task ForeignFinalReply_IsAttachedToNextSend()
    {
        await DeliverAsync(Reply("cli-session", "m1"));

        var sent = await SendAsync("blazor-session");

        Assert.IsNotNull(sent.RelatedReplies);
        Assert.AreEqual(1, sent.RelatedReplies.Count);
        Assert.AreEqual(new ReplyReference("cli-session", "m1"), sent.RelatedReplies[0]);
    }

    [TestMethod]
    public async Task SendTurnAsync_AttachesToo()
    {
        await DeliverAsync(Reply("cli-session", "m1"));

        await _service.SendTurnAsync(Message("blazor-session"),
            new TurnOptions { Timeout = TimeSpan.FromMilliseconds(20) });

        var sent = _publisher.Published[^1].Envelope.GetPayload<UserMessage>()!;
        Assert.AreEqual(new ReplyReference("cli-session", "m1"), sent.RelatedReplies!.Single());
        Assert.IsTrue(sent.ClientCapabilities.HasFlag(ClientCapabilities.AgentEvents));
    }

    [TestMethod]
    public async Task OwnSessionReply_IsNotAttached()
    {
        await DeliverAsync(Reply("blazor-session", "m1"));
        await DeliverAsync(Reply("session/blazor-session", "m2"));

        var sent = await SendAsync("blazor-session");

        Assert.IsNull(sent.RelatedReplies);
    }

    [TestMethod]
    public async Task NonFinalReply_IsNotAttached()
    {
        await DeliverAsync(Reply("cli-session", "m1") with { IsFinal = false });

        var sent = await SendAsync("blazor-session");

        Assert.IsNull(sent.RelatedReplies);
    }

    [TestMethod]
    public async Task ReplyWithoutMessageId_IsNotAttached()
    {
        await DeliverAsync(Reply("cli-session", null));

        var sent = await SendAsync("blazor-session");

        Assert.IsNull(sent.RelatedReplies);
    }

    [TestMethod]
    public async Task McpQuestionForm_IsNotAttached()
    {
        await DeliverAsync(Reply("cli-session", "m1") with { ContentType = McpQuestionForm.ContentType });

        var sent = await SendAsync("blazor-session");

        Assert.IsNull(sent.RelatedReplies);
    }

    [TestMethod]
    public async Task ReplyTheFrontendDidNotShow_IsNotAttached()
    {
        _frontend.Shows = (session, reply) => reply.SessionId == "scheduled";
        await DeliverAsync(Reply("cli-session", "m1"));
        await DeliverAsync(Reply("scheduled", "m2"));

        var sent = await SendAsync("thread-1");

        Assert.AreEqual(new ReplyReference("scheduled", "m2"), sent.RelatedReplies!.Single());
    }

    [TestMethod]
    public async Task CorrelatedReply_IsNotAttached()
    {
        var turn = _service.SendTurnAsync(Message("cli-session"), new TurnOptions { Timeout = TimeSpan.FromSeconds(5) });
        var correlationId = _publisher.Published[^1].Envelope.CorrelationId!;
        await _subscriber.CapturedHandler!(
            TestEnvelopeHelper.CreateEnvelope(Reply("cli-session", "m1"), correlationId: correlationId),
            CancellationToken.None);
        await turn;

        var sent = await SendAsync("blazor-session");

        Assert.IsNull(sent.RelatedReplies);
    }

    [TestMethod]
    public async Task Cursor_MovesOnAfterASend()
    {
        await DeliverAsync(Reply("cli-session", "m1"));
        var first = await SendAsync("blazor-session");
        var second = await SendAsync("blazor-session");
        await DeliverAsync(Reply("cli-session", "m2"));
        var third = await SendAsync("blazor-session");

        Assert.AreEqual(1, first.RelatedReplies?.Count);
        Assert.IsNull(second.RelatedReplies);
        Assert.AreEqual(new ReplyReference("cli-session", "m2"), third.RelatedReplies!.Single());
    }

    [TestMethod]
    public async Task Cursors_AreKeptPerSession()
    {
        await DeliverAsync(Reply("scheduled", "m1"));
        var threadA = await SendAsync("thread-a");
        var threadB = await SendAsync("thread-b");
        var threadAAgain = await SendAsync("thread-a");

        Assert.AreEqual(new ReplyReference("scheduled", "m1"), threadA.RelatedReplies!.Single());
        Assert.AreEqual(new ReplyReference("scheduled", "m1"), threadB.RelatedReplies!.Single());
        Assert.IsNull(threadAAgain.RelatedReplies);
    }

    [TestMethod]
    public async Task AtMostFive_NewestKept_MergedAfterCallersOwn()
    {
        for (var i = 1; i <= 7; i++)
            await DeliverAsync(Reply("cli-session", $"m{i}"));

        var own = new ReplyReference("other", "x");
        var sent = await SendAsync("blazor-session", [own, new ReplyReference("cli-session", "m7")]);

        CollectionAssert.AreEqual(
            new[]
            {
                own,
                new ReplyReference("cli-session", "m7"),
                new ReplyReference("cli-session", "m3"),
                new ReplyReference("cli-session", "m4"),
                new ReplyReference("cli-session", "m5"),
                new ReplyReference("cli-session", "m6"),
            },
            sent.RelatedReplies!.ToArray());
    }

    [TestMethod]
    public async Task NamespacedSessionId_IsReferencedRaw()
    {
        await DeliverAsync(Reply("session/cli-session", "m1"));

        var sent = await SendAsync("blazor-session");

        Assert.AreEqual(new ReplyReference("cli-session", "m1"), sent.RelatedReplies!.Single());
    }

    [TestMethod]
    public async Task FrontendThatThrows_StillSends()
    {
        _frontend.Shows = (_, _) => throw new InvalidOperationException("boom");
        await DeliverAsync(Reply("cli-session", "m1"));

        var sent = await SendAsync("blazor-session");

        Assert.IsNull(sent.RelatedReplies);
    }

    private static AgentReply Reply(string sessionId, string? messageId) => new()
    {
        Content = "result",
        SessionId = sessionId,
        AgentName = "RockBot",
        IsFinal = true,
        MessageId = messageId,
    };

    private static UserMessage Message(string sessionId, IReadOnlyList<ReplyReference>? related = null) => new()
    {
        Content = "summarize that",
        SessionId = sessionId,
        UserId = "u1",
        RelatedReplies = related,
    };

    private Task DeliverAsync(AgentReply reply) =>
        _subscriber.CapturedHandler!(TestEnvelopeHelper.CreateEnvelope(reply, source: "RockBot"), CancellationToken.None);

    private async Task<UserMessage> SendAsync(string sessionId, IReadOnlyList<ReplyReference>? related = null)
    {
        await _service.SendFireAndForgetAsync(Message(sessionId, related));
        return _publisher.Published[^1].Envelope.GetPayload<UserMessage>()!;
    }
}
