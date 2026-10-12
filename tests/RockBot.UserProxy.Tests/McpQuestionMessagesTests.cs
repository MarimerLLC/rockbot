using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Messaging;

namespace RockBot.UserProxy.Tests;

/// <summary>
/// The wire half of answering a handed-back MCP question in the chat's form (#660): the form,
/// answer and status messages, the capability bit, and the proxy's routing of them.
/// </summary>
[TestClass]
public sealed class McpQuestionMessagesTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private TrackingPublisher _publisher = null!;
    private StubUserFrontend _frontend = null!;
    private UserProxyOptions _options = null!;
    private UserProxyService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _publisher = new TrackingPublisher();
        _frontend = new StubUserFrontend();
        _options = new UserProxyOptions { ProxyId = "test-proxy" };
        _service = new UserProxyService(_publisher, new StubSubscriber(), _frontend, _options,
            NullLogger<UserProxyService>.Instance);
    }

    [TestMethod]
    public void Blazor_AdvertisesInteractiveForms_AndTheCliDoesNot()
    {
        Assert.IsTrue(ClientCapabilityPresets.Blazor.HasFlag(ClientCapabilities.InteractiveForms));
        Assert.IsFalse(ClientCapabilityPresets.Cli.HasFlag(ClientCapabilities.InteractiveForms));
        Assert.IsFalse(ClientCapabilityPresets.Teams.HasFlag(ClientCapabilities.InteractiveForms));
    }

    [TestMethod]
    public void TheForm_RoundTripsThroughJson()
    {
        var form = new McpQuestionForm
        {
            QuestionId = "q_1",
            ServerName = "research",
            ToolName = "lookup",
            Message = "Which Mercury?",
            ExpiresAt = DateTimeOffset.Parse("2026-10-12T14:30:00Z"),
            Fields =
            [
                new McpQuestionField
                {
                    Name = "meaning", Kind = McpQuestionFieldKind.SingleSelect, Required = true,
                    Options = [new("planet", "The planet")],
                },
                new McpQuestionField { Name = "ok", Kind = McpQuestionFieldKind.Boolean, IsDecision = true },
                new McpQuestionField { Name = "n", Kind = McpQuestionFieldKind.Integer, Minimum = 1, Maximum = 5, Default = "2" },
            ],
        };

        var json = JsonSerializer.Serialize(form, Json);
        var back = JsonSerializer.Deserialize<McpQuestionForm>(json, Json)!;

        StringAssert.Contains(json, "\"questionId\":\"q_1\"");
        Assert.AreEqual(form.ExpiresAt, back.ExpiresAt);
        Assert.AreEqual(3, back.Fields.Count);
        Assert.AreEqual("The planet", back.Fields[0].Options![0].Label);
        Assert.IsTrue(back.Fields[1].IsDecision);
        Assert.AreEqual(5, back.Fields[2].Maximum);
        Assert.AreEqual("2", back.Fields[2].Default);
    }

    [TestMethod]
    public void OnlyRejected_LeavesTheQuestionOpen()
    {
        Assert.IsFalse(McpQuestionState.IsClosed(null));
        Assert.IsFalse(McpQuestionState.IsClosed(McpQuestionState.Rejected));
        foreach (var state in new[]
                 {
                     McpQuestionState.Answered, McpQuestionState.Declined, McpQuestionState.Expired,
                     McpQuestionState.Cancelled, McpQuestionState.Interrupted, McpQuestionState.Unavailable,
                 })
        {
            Assert.IsTrue(McpQuestionState.IsClosed(state), state);
        }
    }

    [TestMethod]
    public async Task SendMcpAnswer_PublishesToTheAgentsAnswerTopic()
    {
        await _service.SendMcpAnswerAsync(new McpQuestionAnswer
        {
            SessionId = "blazor-session",
            UserId = "blazor-user",
            QuestionId = "q_1",
            Answers = """{"meaning":"planet"}""",
        });

        var (topic, envelope) = _publisher.Published.Single();
        Assert.AreEqual($"{UserProxyTopics.McpAnswer}.{_options.AgentName}", topic);
        Assert.AreEqual(typeof(McpQuestionAnswer).FullName, envelope.MessageType);
        var answer = envelope.GetPayload<McpQuestionAnswer>()!;
        Assert.AreEqual("q_1", answer.QuestionId);
        Assert.AreEqual("""{"meaning":"planet"}""", answer.Answers);
        Assert.IsFalse(answer.Decline);
    }

    [TestMethod]
    public async Task AQuestionStatus_GoesToTheFrontend_NotTheChat()
    {
        var envelope = TestEnvelopeHelper.CreateEnvelope(new McpQuestionStatus
        {
            SessionId = "blazor-session",
            QuestionId = "q_1",
            State = McpQuestionState.Expired,
            Reason = "not answered within 30 minutes",
        });

        var result = await _service.HandleResponseAsync(envelope, CancellationToken.None);

        Assert.AreEqual(MessageResult.Ack, result);
        var status = _frontend.QuestionStatuses.Single();
        Assert.AreEqual("q_1", status.QuestionId);
        Assert.AreEqual(McpQuestionState.Expired, status.State);
        Assert.AreEqual(0, _frontend.DisplayedReplies.Count);
        Assert.AreEqual(0, _frontend.DisplayedErrors.Count);
    }

    [TestMethod]
    public async Task AFormReply_ReachesTheFrontendLikeAnyUnsolicitedReply()
    {
        var reply = new AgentReply
        {
            Content = "The MCP server \"research\" asks: Which Mercury?",
            SessionId = "blazor-session",
            AgentName = "mcp-bridge",
            ContentType = McpQuestionForm.ContentType,
            StructuredData = """{"questionId":"q_1"}""",
        };

        await _service.HandleResponseAsync(TestEnvelopeHelper.CreateEnvelope(reply), CancellationToken.None);

        var shown = _frontend.DisplayedReplies.Single();
        Assert.AreEqual(McpQuestionForm.ContentType, shown.ContentType);
        Assert.AreEqual(reply.StructuredData, shown.StructuredData);
    }
}
