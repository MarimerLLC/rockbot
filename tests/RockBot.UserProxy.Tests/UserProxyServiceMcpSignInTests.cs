using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

/// <summary>
/// The user proxy's side of MCP OAuth sign-in (#738): requests go to the agent's sign-in topic and
/// their replies come back on the proxy's point-to-point topic; outcomes arrive on the broadcast
/// topic, for the frontend and for whoever waits on the sign-in.
/// </summary>
[TestClass]
public sealed class UserProxyServiceMcpSignInTests
{
    private TrackingPublisher _publisher = null!;
    private StubSubscriber _subscriber = null!;
    private StubUserFrontend _frontend = null!;
    private UserProxyOptions _options = null!;
    private UserProxyService _service = null!;

    private string SignInTopic => $"{UserProxyTopics.McpSignIn}.{_options.AgentName}";
    private string BroadcastTopic => $"{UserProxyTopics.UserResponse}.{_options.AgentName}";
    private string PointToPointTopic => $"{UserProxyTopics.UserResponse}.{_options.AgentName}.{_options.ProxyId}";

    [TestInitialize]
    public async Task Setup()
    {
        _publisher = new TrackingPublisher();
        _subscriber = new StubSubscriber();
        _frontend = new StubUserFrontend();
        _options = new UserProxyOptions { ProxyId = "test-proxy" };
        _service = new UserProxyService(_publisher, _subscriber, _frontend, _options, NullLogger<UserProxyService>.Instance);
        await _service.StartAsync(CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup() => await _service.StopAsync(CancellationToken.None);

    private async Task<MessageEnvelope> WaitForPublishAsync()
    {
        for (var i = 0; i < 200 && _publisher.Published.Count == 0; i++)
            await Task.Delay(10);
        return _publisher.Published.Single().Envelope;
    }

    private Task DeliverAsync(string topic, MessageEnvelope envelope) =>
        _subscriber.GetHandlerForTopic(topic)!(envelope, CancellationToken.None);

    [TestMethod]
    public async Task StartSignIn_AsksTheAgent_AndReturnsItsCorrelatedReply()
    {
        var pending = _service.StartMcpSignInAsync("tasks", "https://app/mcp-oauth/callback", TimeSpan.FromSeconds(5));
        var request = await WaitForPublishAsync();

        Assert.AreEqual(SignInTopic, _publisher.Published[0].Topic);
        Assert.AreEqual(PointToPointTopic, request.ReplyTo);
        var payload = request.GetPayload<McpSignInStartRequest>()!;
        Assert.AreEqual("tasks", payload.ServerName);
        Assert.AreEqual("https://app/mcp-oauth/callback", payload.RedirectUri);

        await DeliverAsync(PointToPointTopic, new McpSignInStartResponse
        {
            ServerName = "tasks",
            SignInId = "s1",
            AuthorizationUri = "https://as/authorize?state=x",
        }.ToEnvelope(source: "bridge", correlationId: request.CorrelationId));

        var response = await pending;
        Assert.AreEqual("s1", response!.SignInId);
        Assert.AreEqual("https://as/authorize?state=x", response.AuthorizationUri);
        Assert.AreEqual(0, _frontend.DisplayedReplies.Count, "a sign-in reply is not a chat message");
    }

    [TestMethod]
    public async Task StartSignIn_ReturnsNull_WhenTheAgentDoesNotAnswer()
    {
        var response = await _service.StartMcpSignInAsync("tasks", "https://app/cb", TimeSpan.FromMilliseconds(50));
        Assert.IsNull(response);
    }

    [TestMethod]
    public async Task Callback_IsRelayedToTheAgent()
    {
        await _service.SendMcpSignInCallbackAsync(new McpSignInCallback { State = "st", Code = "c", Iss = "https://as" });

        var (topic, envelope) = _publisher.Published.Single();
        Assert.AreEqual(SignInTopic, topic);
        var callback = envelope.GetPayload<McpSignInCallback>()!;
        Assert.AreEqual("st", callback.State);
        Assert.AreEqual("c", callback.Code);
        Assert.AreEqual("https://as", callback.Iss);
    }

    [TestMethod]
    public async Task Status_ReachesTheFrontend_AndSettlesItsWaiter()
    {
        var wait = _service.WaitForMcpSignInAsync("s1", TimeSpan.FromSeconds(5));

        await DeliverAsync(BroadcastTopic, new McpSignInStatus
        {
            ServerName = "tasks", State = McpSignInState.Completed, SignInId = "s1",
        }.ToEnvelope<McpSignInStatus>(source: "agent"));

        var outcome = await wait;
        Assert.AreEqual(McpSignInState.Completed, outcome!.State);
        Assert.AreEqual("tasks", _frontend.SignInStatuses.Single().ServerName);
        Assert.AreEqual(0, _frontend.DisplayedReplies.Count);
    }

    [TestMethod]
    public async Task Status_ThatSettledBeforeAnyoneWaited_IsStillSeen()
    {
        await DeliverAsync(BroadcastTopic, new McpSignInStatus
        {
            ServerName = "tasks", State = McpSignInState.Failed, SignInId = "s2", Message = "refused",
        }.ToEnvelope<McpSignInStatus>(source: "agent"));

        var outcome = await _service.WaitForMcpSignInAsync("s2", TimeSpan.FromMilliseconds(100));
        Assert.AreEqual(McpSignInState.Failed, outcome!.State);
        Assert.AreEqual("refused", outcome.Message);
    }

    [TestMethod]
    public async Task WaitForSignIn_ReturnsNull_WhenNothingSettlesIt()
    {
        await DeliverAsync(BroadcastTopic, new McpSignInStatus
        {
            ServerName = "tasks", State = McpSignInState.Required,
        }.ToEnvelope<McpSignInStatus>(source: "agent"));

        Assert.IsNull(await _service.WaitForMcpSignInAsync("s3", TimeSpan.FromMilliseconds(50)));
        Assert.AreEqual(McpSignInState.Required, _frontend.SignInStatuses.Single().State);
    }
}
