using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.A2A.Tests;

[TestClass]
public class AgentTaskRequestHandlerTests
{
    private readonly StubAgentTaskHandler _taskHandler = new();
    private readonly TrackingPublisher _publisher = new();
    private readonly A2AOptions _options = new();
    private readonly AgentIdentity _agent = new("test-a2a-agent");
    private readonly NullLogger<AgentTaskRequestHandler> _logger = NullLogger<AgentTaskRequestHandler>.Instance;

    private AgentTaskRequestHandler CreateHandler() =>
        new(_taskHandler, _publisher, _options, _agent, _logger);

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

    private static AgentTaskRequest CreateRequest(string taskId = "task-1") =>
        new()
        {
            TaskId = taskId,
            Skill = "summarize",
            Message = new AgentMessage
            {
                Role = "user",
                Parts = [new AgentMessagePart { Kind = "text", Text = "Hello" }]
            }
        };

    [TestMethod]
    public async Task PublishesResult_ToReplyToTopic()
    {
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "custom.reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        // Should have: 1 status update (Working) + 1 result
        Assert.AreEqual(2, _publisher.Published.Count);
        Assert.AreEqual("custom.reply", _publisher.Published[1].Topic);

        var result = _publisher.Published[1].Envelope.GetPayload<AgentTaskResult>();
        Assert.IsNotNull(result);
        Assert.AreEqual("task-1", result.TaskId);
        Assert.AreEqual(AgentTaskState.Completed, result.State);
    }

    [TestMethod]
    public async Task FallsBackToDefaultResultTopic_WhenNoReplyTo()
    {
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request);
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(2, _publisher.Published.Count);
        Assert.AreEqual(_options.DefaultResultTopic, _publisher.Published[1].Topic);
    }

    [TestMethod]
    public async Task PreservesCorrelationId_OnResponses()
    {
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, correlationId: "corr-123", replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        // Both status and result should have the correlation ID
        Assert.AreEqual("corr-123", _publisher.Published[0].Envelope.CorrelationId);
        Assert.AreEqual("corr-123", _publisher.Published[1].Envelope.CorrelationId);
    }

    [TestMethod]
    public async Task PublishesWorkingStatusUpdate_BeforeDelegating()
    {
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.IsTrue(_publisher.Published.Count >= 2);
        Assert.AreEqual(_options.StatusTopic, _publisher.Published[0].Topic);

        var status = _publisher.Published[0].Envelope.GetPayload<AgentTaskStatusUpdate>();
        Assert.IsNotNull(status);
        Assert.AreEqual(AgentTaskState.Working, status.State);
        Assert.AreEqual("task-1", status.TaskId);
    }

    [TestMethod]
    public async Task PublishesAgentTaskError_OnHandlerException()
    {
        _taskHandler.ExceptionToThrow = new InvalidOperationException("Something broke");
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        // Status update + error
        Assert.AreEqual(2, _publisher.Published.Count);
        Assert.AreEqual("reply", _publisher.Published[1].Topic);

        var error = _publisher.Published[1].Envelope.GetPayload<AgentTaskError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(AgentTaskError.Codes.ExecutionFailed, error.Code);
        Assert.AreEqual("task-1", error.TaskId);
        Assert.AreEqual("Something broke", error.Message);
    }

    [TestMethod]
    public async Task SetsSource_ToAgentName()
    {
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        foreach (var (_, published) in _publisher.Published)
        {
            Assert.AreEqual("test-a2a-agent", published.Source);
        }
    }

    [TestMethod]
    public async Task PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _taskHandler.ExceptionToThrow = new OperationCanceledException(cts.Token);
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => handler.HandleAsync(request, CreateContext(envelope, cts.Token)));
    }

    [TestMethod]
    public async Task PublishStatusDelegate_PublishesToStatusTopic()
    {
        AgentTaskContext? capturedContext = null;
        _taskHandler.ResultToReturn = null;

        // Create a handler that captures the context and calls PublishStatus
        var customHandler = new DelegatingTaskHandler(async (req, ctx) =>
        {
            capturedContext = ctx;
            await ctx.PublishStatus(new AgentTaskStatusUpdate
            {
                TaskId = req.TaskId,
                State = AgentTaskState.InputRequired,
                Message = new AgentMessage
                {
                    Role = "agent",
                    Parts = [new AgentMessagePart { Kind = "text", Text = "Need more info" }]
                }
            }, ctx.MessageContext.CancellationToken);

            return new AgentTaskResult
            {
                TaskId = req.TaskId,
                State = AgentTaskState.Completed
            };
        });

        var handler = new AgentTaskRequestHandler(
            customHandler, _publisher, _options, _agent, _logger);
        var request = CreateRequest();
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");

        await handler.HandleAsync(request, CreateContext(envelope));

        // Working status + custom InputRequired status + result = 3
        Assert.AreEqual(3, _publisher.Published.Count);
        Assert.AreEqual(_options.StatusTopic, _publisher.Published[1].Topic);

        var customStatus = _publisher.Published[1].Envelope.GetPayload<AgentTaskStatusUpdate>();
        Assert.IsNotNull(customStatus);
        Assert.AreEqual(AgentTaskState.InputRequired, customStatus.State);
    }

    private sealed class DelegatingTaskHandler(
        Func<AgentTaskRequest, AgentTaskContext, Task<AgentTaskResult>> handler) : IAgentTaskHandler
    {
        public Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context) =>
            handler(request, context);
    }
}
