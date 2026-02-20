using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;
using RockBot.Tools.Mcp;

namespace RockBot.Tools.Tests;

[TestClass]
public class McpToolProxyTests
{
    private readonly TrackingPublisher _publisher = new();
    private readonly StubSubscriber _subscriber = new();
    private readonly AgentIdentity _identity = new("test-agent");

    private McpToolProxy CreateProxy(TimeSpan? timeout = null)
    {
        return new McpToolProxy(
            _publisher,
            _subscriber,
            _identity,
            NullLogger<McpToolProxy>.Instance,
            timeout);
    }

    [TestMethod]
    public async Task ExecuteAsync_PublishesRequestToCorrectTopic()
    {
        var proxy = CreateProxy();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "read_file",
            Arguments = """{"path":"/tmp/test.txt"}"""
        };

        // Start execution (will block waiting for response)
        var executeTask = proxy.ExecuteAsync(request, CancellationToken.None);

        // Wait for the subscription to be set up and request published
        await Task.Delay(100);

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual(McpToolProxy.InvokeTopic, _publisher.Published[0].Topic);

        var envelope = _publisher.Published[0].Envelope;
        var payload = envelope.GetPayload<ToolInvokeRequest>();
        Assert.IsNotNull(payload);
        Assert.AreEqual("read_file", payload.ToolName);
        Assert.AreEqual("call-1", payload.ToolCallId);

        // Simulate response to unblock
        var correlationId = envelope.CorrelationId!;
        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-1",
            ToolName = "read_file",
            Content = "file contents"
        };
        var responseEnvelope = response.ToEnvelope(
            "bridge",
            correlationId: correlationId);

        await _subscriber.DeliverAsync($"tool.result.{_identity.Name}", responseEnvelope);

        var result = await executeTask;
        Assert.AreEqual("file contents", result.Content);
        Assert.IsFalse(result.IsError);
    }

    [TestMethod]
    public async Task ExecuteAsync_SetsContentTrustHeader()
    {
        var proxy = CreateProxy();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "test_tool"
        };

        var executeTask = proxy.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        var envelope = _publisher.Published[0].Envelope;
        Assert.AreEqual(
            WellKnownHeaders.ContentTrustValues.ToolRequest,
            envelope.Headers[WellKnownHeaders.ContentTrust]);

        // Clean up
        var correlationId = envelope.CorrelationId!;
        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-1",
            ToolName = "test_tool",
            Content = "ok"
        };
        await _subscriber.DeliverAsync(
            $"tool.result.{_identity.Name}",
            response.ToEnvelope("bridge", correlationId: correlationId));
        await executeTask;
    }

    [TestMethod]
    public async Task ExecuteAsync_SetsToolProviderHeader()
    {
        var proxy = CreateProxy();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "test_tool"
        };

        var executeTask = proxy.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        var envelope = _publisher.Published[0].Envelope;
        Assert.AreEqual("mcp", envelope.Headers[WellKnownHeaders.ToolProvider]);

        // Clean up
        var correlationId = envelope.CorrelationId!;
        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-1",
            ToolName = "test_tool",
            Content = "ok"
        };
        await _subscriber.DeliverAsync(
            $"tool.result.{_identity.Name}",
            response.ToEnvelope("bridge", correlationId: correlationId));
        await executeTask;
    }

    [TestMethod]
    public async Task ExecuteAsync_SetsReplyToTopic()
    {
        var proxy = CreateProxy();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "test_tool"
        };

        var executeTask = proxy.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        var envelope = _publisher.Published[0].Envelope;
        Assert.AreEqual($"tool.result.{_identity.Name}", envelope.ReplyTo);

        // Clean up
        var correlationId = envelope.CorrelationId!;
        var response = new ToolInvokeResponse
        {
            ToolCallId = "call-1",
            ToolName = "test_tool",
            Content = "ok"
        };
        await _subscriber.DeliverAsync(
            $"tool.result.{_identity.Name}",
            response.ToEnvelope("bridge", correlationId: correlationId));
        await executeTask;
    }

    [TestMethod]
    public async Task ExecuteAsync_Timeout_ReturnsErrorResponse()
    {
        var proxy = CreateProxy(timeout: TimeSpan.FromMilliseconds(100));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "slow_tool"
        };

        var result = await proxy.ExecuteAsync(request, CancellationToken.None);

        Assert.IsTrue(result.IsError);
        Assert.IsTrue(result.Content?.Contains("timed out"));
        Assert.AreEqual("call-1", result.ToolCallId);
        Assert.AreEqual("slow_tool", result.ToolName);
    }

    [TestMethod]
    public async Task ExecuteAsync_ToolError_ReturnsErrorResponse()
    {
        var proxy = CreateProxy();

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "failing_tool"
        };

        var executeTask = proxy.ExecuteAsync(request, CancellationToken.None);
        await Task.Delay(100);

        var correlationId = _publisher.Published[0].Envelope.CorrelationId!;

        var error = new ToolError
        {
            ToolCallId = "call-1",
            ToolName = "failing_tool",
            Code = ToolError.Codes.ExecutionFailed,
            Message = "Something went wrong",
            IsRetryable = false
        };

        var errorEnvelope = error.ToEnvelope("bridge", correlationId: correlationId);
        await _subscriber.DeliverAsync($"tool.result.{_identity.Name}", errorEnvelope);

        var result = await executeTask;
        Assert.IsTrue(result.IsError);
        Assert.AreEqual("Something went wrong", result.Content);
    }

    [TestMethod]
    public void ResponseTopic_IncludesAgentName()
    {
        var proxy = CreateProxy();
        Assert.AreEqual("tool.result.test-agent", proxy.ResponseTopic);
    }

    [TestMethod]
    public async Task ExecuteAsync_SubscribesToResponseTopic()
    {
        var proxy = CreateProxy(timeout: TimeSpan.FromMilliseconds(50));

        var request = new ToolInvokeRequest
        {
            ToolCallId = "call-1",
            ToolName = "test_tool"
        };

        // Will timeout but triggers subscription setup
        await proxy.ExecuteAsync(request, CancellationToken.None);

        Assert.AreEqual(1, _subscriber.Subscriptions.Count);
        Assert.AreEqual($"tool.result.{_identity.Name}", _subscriber.Subscriptions[0].Topic);
    }
}
