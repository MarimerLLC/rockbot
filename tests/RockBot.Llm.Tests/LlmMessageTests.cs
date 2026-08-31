using RockBot.Messaging;

namespace RockBot.Llm.Tests;

[TestClass]
public class LlmMessageTests
{
    [TestMethod]
    public void LlmRequest_RoundTrips()
    {
        var request = new LlmRequest
        {
            Messages =
            [
                new LlmChatMessage { Role = "system", Content = "You are helpful." },
                new LlmChatMessage { Role = "user", Content = "Hello" }
            ],
            ModelId = "gpt-4o-mini",
            Temperature = 0.7f,
            MaxOutputTokens = 1024,
            StopSequences = ["STOP"]
        };

        var envelope = request.ToEnvelope<LlmRequest>("test");
        var deserialized = envelope.GetPayload<LlmRequest>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(2, deserialized.Messages.Count);
        Assert.AreEqual("system", deserialized.Messages[0].Role);
        Assert.AreEqual("You are helpful.", deserialized.Messages[0].Content);
        Assert.AreEqual("user", deserialized.Messages[1].Role);
        Assert.AreEqual("Hello", deserialized.Messages[1].Content);
        Assert.AreEqual("gpt-4o-mini", deserialized.ModelId);
        Assert.AreEqual(0.7f, deserialized.Temperature);
        Assert.AreEqual(1024, deserialized.MaxOutputTokens);
        Assert.AreEqual(1, deserialized.StopSequences!.Count);
        Assert.AreEqual("STOP", deserialized.StopSequences[0]);
    }

    [TestMethod]
    public void LlmRequest_WithTools_RoundTrips()
    {
        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "What's the weather?" }],
            Tools =
            [
                new LlmToolDefinition
                {
                    Name = "get_weather",
                    Description = "Get current weather",
                    ParametersSchema = """{"type":"object","properties":{"city":{"type":"string"}}}"""
                }
            ]
        };

        var envelope = request.ToEnvelope<LlmRequest>("test");
        var deserialized = envelope.GetPayload<LlmRequest>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(1, deserialized.Tools!.Count);
        Assert.AreEqual("get_weather", deserialized.Tools[0].Name);
        Assert.AreEqual("Get current weather", deserialized.Tools[0].Description);
        Assert.IsNotNull(deserialized.Tools[0].ParametersSchema);
    }

    [TestMethod]
    public void LlmResponse_RoundTrips()
    {
        var response = new LlmResponse
        {
            Content = "Hello! How can I help?",
            FinishReason = "stop",
            Usage = new LlmUsage { InputTokens = 10, OutputTokens = 8, TotalTokens = 18 },
            ModelId = "gpt-4o-mini"
        };

        var envelope = response.ToEnvelope<LlmResponse>("test");
        var deserialized = envelope.GetPayload<LlmResponse>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("Hello! How can I help?", deserialized.Content);
        Assert.AreEqual("stop", deserialized.FinishReason);
        Assert.IsNotNull(deserialized.Usage);
        Assert.AreEqual(10, deserialized.Usage.InputTokens);
        Assert.AreEqual(8, deserialized.Usage.OutputTokens);
        Assert.AreEqual(18, deserialized.Usage.TotalTokens);
        Assert.AreEqual("gpt-4o-mini", deserialized.ModelId);
    }

    [TestMethod]
    public void LlmResponse_WithToolCalls_RoundTrips()
    {
        var response = new LlmResponse
        {
            ToolCalls =
            [
                new LlmToolCall
                {
                    Id = "call_1",
                    Name = "get_weather",
                    Arguments = """{"city":"Seattle"}"""
                }
            ],
            FinishReason = "tool_calls"
        };

        var envelope = response.ToEnvelope<LlmResponse>("test");
        var deserialized = envelope.GetPayload<LlmResponse>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(1, deserialized.ToolCalls!.Count);
        Assert.AreEqual("call_1", deserialized.ToolCalls[0].Id);
        Assert.AreEqual("get_weather", deserialized.ToolCalls[0].Name);
        Assert.AreEqual("""{"city":"Seattle"}""", deserialized.ToolCalls[0].Arguments);
        Assert.AreEqual("tool_calls", deserialized.FinishReason);
    }

    [TestMethod]
    public void LlmError_RoundTrips()
    {
        var error = new LlmError
        {
            Code = LlmError.Codes.RateLimited,
            Message = "Too many requests",
            IsRetryable = true
        };

        var envelope = error.ToEnvelope<LlmError>("test");
        var deserialized = envelope.GetPayload<LlmError>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(LlmError.Codes.RateLimited, deserialized.Code);
        Assert.AreEqual("Too many requests", deserialized.Message);
        Assert.IsTrue(deserialized.IsRetryable);
    }

    [TestMethod]
    public void LlmChatMessage_WithToolCalls_RoundTrips()
    {
        var message = new LlmChatMessage
        {
            Role = "assistant",
            Content = "I'll check that for you.",
            ToolCalls =
            [
                new LlmToolCall { Id = "call_1", Name = "search", Arguments = """{"query":"test"}""" }
            ]
        };

        var envelope = message.ToEnvelope<LlmChatMessage>("test");
        var deserialized = envelope.GetPayload<LlmChatMessage>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("assistant", deserialized.Role);
        Assert.AreEqual("I'll check that for you.", deserialized.Content);
        Assert.AreEqual(1, deserialized.ToolCalls!.Count);
        Assert.AreEqual("call_1", deserialized.ToolCalls[0].Id);
    }

    [TestMethod]
    public void LlmChatMessage_ToolResult_RoundTrips()
    {
        var message = new LlmChatMessage
        {
            Role = "tool",
            Content = "The weather is sunny.",
            ToolCallId = "call_1"
        };

        var envelope = message.ToEnvelope<LlmChatMessage>("test");
        var deserialized = envelope.GetPayload<LlmChatMessage>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("tool", deserialized.Role);
        Assert.AreEqual("The weather is sunny.", deserialized.Content);
        Assert.AreEqual("call_1", deserialized.ToolCallId);
    }

    [TestMethod]
    public void LlmUsage_RoundTrips()
    {
        var usage = new LlmUsage
        {
            InputTokens = 100,
            OutputTokens = 50,
            TotalTokens = 150
        };

        var envelope = usage.ToEnvelope<LlmUsage>("test");
        var deserialized = envelope.GetPayload<LlmUsage>();

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(100, deserialized.InputTokens);
        Assert.AreEqual(50, deserialized.OutputTokens);
        Assert.AreEqual(150, deserialized.TotalTokens);
    }

    [TestMethod]
    public void LlmRequest_OptionalFields_DefaultToNull()
    {
        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };

        var envelope = request.ToEnvelope<LlmRequest>("test");
        var deserialized = envelope.GetPayload<LlmRequest>();

        Assert.IsNotNull(deserialized);
        Assert.IsNull(deserialized.ModelId);
        Assert.IsNull(deserialized.Temperature);
        Assert.IsNull(deserialized.MaxOutputTokens);
        Assert.IsNull(deserialized.Tools);
        Assert.IsNull(deserialized.StopSequences);
    }
}
