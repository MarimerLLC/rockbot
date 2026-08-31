using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class AgentTaskCancelHandlerTests
{
    private readonly TrackingPublisher _publisher = new();
    private readonly A2AOptions _options = new();
    private readonly AgentIdentity _agent = new("test-a2a-agent");
    private readonly NullLogger<AgentTaskCancelHandler> _logger = NullLogger<AgentTaskCancelHandler>.Instance;

    private AgentTaskCancelHandler CreateHandler() =>
        new(_publisher, _options, _agent, _logger);

    private static MessageHandlerContext CreateContext(
        MessageEnvelope envelope,
        CancellationToken ct = default) =>
        new()
        {
            Envelope = envelope,
            Agent = new AgentIdentity("test-agent"),
            Services = null!,
            CancellationToken = ct
        };

    [TestMethod]
    public async Task PublishesTaskNotCancelableError()
    {
        var request = new AgentTaskCancelRequest
        {
            TaskId = "task-1",
            ContextId = "ctx-1"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual("reply", _publisher.Published[0].Topic);

        var error = _publisher.Published[0].Envelope.GetPayload<AgentTaskError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(AgentTaskError.Codes.TaskNotCancelable, error.Code);
        Assert.AreEqual("task-1", error.TaskId);
        Assert.AreEqual("ctx-1", error.ContextId);
        Assert.IsFalse(error.IsRetryable);
    }

    [TestMethod]
    public async Task FallsBackToDefaultResultTopic_WhenNoReplyTo()
    {
        var request = new AgentTaskCancelRequest { TaskId = "task-2" };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request);
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual(_options.DefaultResultTopic, _publisher.Published[0].Topic);
    }

    [TestMethod]
    public async Task PreservesCorrelationId()
    {
        var request = new AgentTaskCancelRequest { TaskId = "task-3" };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, correlationId: "corr-456", replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("corr-456", _publisher.Published[0].Envelope.CorrelationId);
    }

    [TestMethod]
    public async Task SetsSource_ToAgentName()
    {
        var request = new AgentTaskCancelRequest { TaskId = "task-4" };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("test-a2a-agent", _publisher.Published[0].Envelope.Source);
    }
}
