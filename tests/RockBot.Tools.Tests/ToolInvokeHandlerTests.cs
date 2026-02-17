using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools;

namespace RockBot.Tools.Tests;

[TestClass]
public class ToolInvokeHandlerTests
{
    private readonly ToolRegistry _registry = new();
    private readonly TrackingPublisher _publisher = new();
    private readonly ToolOptions _options = new();
    private readonly AgentIdentity _agent = new("test-tool-agent");
    private readonly NullLogger<ToolInvokeHandler> _logger = NullLogger<ToolInvokeHandler>.Instance;

    private ToolInvokeHandler CreateHandler() =>
        new(_registry, _publisher, _options, _agent, _logger);

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
    public async Task PublishesResponse_ToReplyToTopic()
    {
        var executor = new StubToolExecutor
        {
            ResponseToReturn = new ToolInvokeResponse
            {
                ToolCallId = "call_1",
                ToolName = "test_tool",
                Content = "result data"
            }
        };
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool",
            Arguments = """{"key": "value"}"""
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "custom.reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual("custom.reply", _publisher.Published[0].Topic);

        var response = _publisher.Published[0].Envelope.GetPayload<ToolInvokeResponse>();
        Assert.IsNotNull(response);
        Assert.AreEqual("call_1", response.ToolCallId);
        Assert.AreEqual("result data", response.Content);
    }

    [TestMethod]
    public async Task PublishesResponse_FallsBackToDefaultTopic()
    {
        _options.DefaultResultTopic = "tool.result";
        var executor = new StubToolExecutor();
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request); // No ReplyTo
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("tool.result", _publisher.Published[0].Topic);
    }

    [TestMethod]
    public async Task PublishesToolError_WhenToolNotFound()
    {
        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "nonexistent_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual("reply", _publisher.Published[0].Topic);

        var error = _publisher.Published[0].Envelope.GetPayload<ToolError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(ToolError.Codes.ToolNotFound, error.Code);
        Assert.AreEqual("call_1", error.ToolCallId);
        Assert.AreEqual("nonexistent_tool", error.ToolName);
        Assert.IsFalse(error.IsRetryable);
    }

    [TestMethod]
    public async Task PublishesToolError_OnExecutionException()
    {
        var executor = new StubToolExecutor
        {
            ExceptionToThrow = new Exception("Something broke")
        };
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _publisher.Published.Count);

        var error = _publisher.Published[0].Envelope.GetPayload<ToolError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(ToolError.Codes.ExecutionFailed, error.Code);
        Assert.AreEqual("Something broke", error.Message);
    }

    [TestMethod]
    public async Task ClassifiesTimeoutException()
    {
        var executor = new StubToolExecutor
        {
            ExceptionToThrow = new TimeoutException("Timed out")
        };
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        var error = _publisher.Published[0].Envelope.GetPayload<ToolError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(ToolError.Codes.Timeout, error.Code);
        Assert.IsTrue(error.IsRetryable);
    }

    [TestMethod]
    public async Task ClassifiesArgumentException()
    {
        var executor = new StubToolExecutor
        {
            ExceptionToThrow = new ArgumentException("Bad args")
        };
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        var error = _publisher.Published[0].Envelope.GetPayload<ToolError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(ToolError.Codes.InvalidArguments, error.Code);
        Assert.IsFalse(error.IsRetryable);
    }

    [TestMethod]
    public async Task SetsCorrelationId_FromOriginalEnvelope()
    {
        var executor = new StubToolExecutor();
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(
            request, correlationId: "corr-789", replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("corr-789", _publisher.Published[0].Envelope.CorrelationId);
    }

    [TestMethod]
    public async Task SetsSource_ToAgentName()
    {
        var executor = new StubToolExecutor();
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("test-tool-agent", _publisher.Published[0].Envelope.Source);
    }

    [TestMethod]
    public async Task HostShutdown_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var executor = new StubToolExecutor
        {
            ExceptionToThrow = new OperationCanceledException(cts.Token)
        };
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool"
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => handler.HandleAsync(request, CreateContext(envelope, cts.Token)));

        Assert.AreEqual(0, _publisher.Published.Count);
    }

    [TestMethod]
    public async Task PassesRequestToExecutor()
    {
        var executor = new StubToolExecutor();
        _registry.Register(new ToolRegistration
        {
            Name = "test_tool",
            Description = "A test tool",
            Source = "test"
        }, executor);

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call_1",
            ToolName = "test_tool",
            Arguments = """{"city": "Seattle"}"""
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, executor.Invocations.Count);
        Assert.AreEqual("call_1", executor.Invocations[0].ToolCallId);
        Assert.AreEqual("test_tool", executor.Invocations[0].ToolName);
        Assert.AreEqual("""{"city": "Seattle"}""", executor.Invocations[0].Arguments);
    }
}
