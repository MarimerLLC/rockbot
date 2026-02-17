using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;
using RockBot.Messaging;

namespace RockBot.Llm.Tests;

[TestClass]
public class LlmRequestHandlerTests
{
    private readonly StubChatClient _chatClient = new();
    private readonly TrackingPublisher _publisher = new();
    private readonly LlmOptions _options = new();
    private readonly AgentIdentity _agent = new("test-llm-agent");
    private readonly NullLogger<LlmRequestHandler> _logger = NullLogger<LlmRequestHandler>.Instance;

    private LlmRequestHandler CreateHandler() =>
        new(_chatClient, _publisher, _options, _agent, _logger);

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
        _chatClient.ResponseToReturn = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Hello!"))
        {
            FinishReason = ChatFinishReason.Stop
        };

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "custom.reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual("custom.reply", _publisher.Published[0].Topic);

        var response = _publisher.Published[0].Envelope.GetPayload<LlmResponse>();
        Assert.IsNotNull(response);
        Assert.AreEqual("Hello!", response.Content);
    }

    [TestMethod]
    public async Task PublishesResponse_FallsBackToDefaultTopic()
    {
        _options.DefaultResponseTopic = "llm.response";
        _chatClient.ResponseToReturn = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Hi"));

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request); // No ReplyTo
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("llm.response", _publisher.Published[0].Topic);
    }

    [TestMethod]
    public async Task SetsCorrelationId_FromOriginalEnvelope()
    {
        _chatClient.ResponseToReturn = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Hi"));

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(
            request, correlationId: "corr-123", replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("corr-123", _publisher.Published[0].Envelope.CorrelationId);
    }

    [TestMethod]
    public async Task MapsToolCalls_InResponse()
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call_1", "get_weather",
                new Dictionary<string, object?> { ["city"] = "Seattle" })
        });
        _chatClient.ResponseToReturn = new ChatResponse(message)
        {
            FinishReason = ChatFinishReason.ToolCalls
        };

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Weather?" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        var response = _publisher.Published[0].Envelope.GetPayload<LlmResponse>();
        Assert.IsNotNull(response);
        Assert.AreEqual("tool_calls", response.FinishReason);
        Assert.AreEqual(1, response.ToolCalls!.Count);
        Assert.AreEqual("call_1", response.ToolCalls[0].Id);
        Assert.AreEqual("get_weather", response.ToolCalls[0].Name);
    }

    [TestMethod]
    public async Task PublishesLlmError_OnProviderFailure()
    {
        _chatClient.ExceptionToThrow = new Exception("Provider down");

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _publisher.Published.Count);
        Assert.AreEqual("reply", _publisher.Published[0].Topic);

        var error = _publisher.Published[0].Envelope.GetPayload<LlmError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(LlmError.Codes.ProviderError, error.Code);
        Assert.AreEqual("Provider down", error.Message);
    }

    [TestMethod]
    public async Task ClassifiesRetryableErrors()
    {
        _chatClient.ExceptionToThrow = new HttpRequestException(
            "Too many", null, System.Net.HttpStatusCode.TooManyRequests);

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        var error = _publisher.Published[0].Envelope.GetPayload<LlmError>();
        Assert.IsNotNull(error);
        Assert.AreEqual(LlmError.Codes.RateLimited, error.Code);
        Assert.IsTrue(error.IsRetryable);
    }

    [TestMethod]
    public async Task PassesTemperatureAndModel_ToProvider()
    {
        _chatClient.ResponseToReturn = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Hi"));

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }],
            ModelId = "gpt-4o",
            Temperature = 0.42f
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual(1, _chatClient.Invocations.Count);
        var options = _chatClient.Invocations[0].Options;
        Assert.IsNotNull(options);
        Assert.AreEqual("gpt-4o", options.ModelId);
        Assert.AreEqual(0.42f, options.Temperature);
    }

    [TestMethod]
    public async Task PassesTools_ToProvider()
    {
        _chatClient.ResponseToReturn = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Hi"));

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }],
            Tools =
            [
                new LlmToolDefinition
                {
                    Name = "search",
                    Description = "Search the web"
                }
            ]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        var options = _chatClient.Invocations[0].Options;
        Assert.IsNotNull(options?.Tools);
        Assert.AreEqual(1, options.Tools.Count);
        Assert.AreEqual("search", options.Tools[0].Name);
    }

    [TestMethod]
    public async Task SetsSource_ToAgentName()
    {
        _chatClient.ResponseToReturn = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Hi"));

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("test-llm-agent", _publisher.Published[0].Envelope.Source);
    }

    [TestMethod]
    public async Task ErrorResponse_SetsCorrelationId()
    {
        _chatClient.ExceptionToThrow = new Exception("fail");

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(
            request, correlationId: "corr-456", replyTo: "reply");
        var handler = CreateHandler();

        await handler.HandleAsync(request, CreateContext(envelope));

        Assert.AreEqual("corr-456", _publisher.Published[0].Envelope.CorrelationId);
    }

    [TestMethod]
    public async Task HostShutdown_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _chatClient.ExceptionToThrow = new OperationCanceledException(cts.Token);

        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var envelope = TestEnvelopeHelper.CreateEnvelope(request, replyTo: "reply");
        var handler = CreateHandler();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => handler.HandleAsync(request, CreateContext(envelope, cts.Token)));

        // Should NOT have published an error — the exception propagated
        Assert.AreEqual(0, _publisher.Published.Count);
    }
}
