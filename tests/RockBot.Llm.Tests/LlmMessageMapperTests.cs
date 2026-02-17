using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RockBot.Llm.Tests;

[TestClass]
public class LlmMessageMapperTests
{
    [TestMethod]
    public void ToChatMessages_MapsSystemRole()
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "system", Content = "Be helpful." }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(ChatRole.System, result[0].Role);
        Assert.AreEqual("Be helpful.", result[0].Text);
    }

    [TestMethod]
    public void ToChatMessages_MapsUserRole()
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "user", Content = "Hello" }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(ChatRole.User, result[0].Role);
        Assert.AreEqual("Hello", result[0].Text);
    }

    [TestMethod]
    public void ToChatMessages_MapsAssistantRole()
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "assistant", Content = "Hi there!" }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(ChatRole.Assistant, result[0].Role);
        Assert.AreEqual("Hi there!", result[0].Text);
    }

    [TestMethod]
    public void ToChatMessages_MapsToolResultMessage()
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "tool", Content = "72°F sunny", ToolCallId = "call_abc" }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(ChatRole.Tool, result[0].Role);
        var frc = result[0].Contents.OfType<FunctionResultContent>().Single();
        Assert.AreEqual("call_abc", frc.CallId);
        Assert.AreEqual("72°F sunny", frc.Result);
    }

    [TestMethod]
    public void ToChatMessages_MapsAssistantWithToolCalls()
    {
        var messages = new List<LlmChatMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "Let me check.",
                ToolCalls =
                [
                    new LlmToolCall
                    {
                        Id = "call_1",
                        Name = "get_weather",
                        Arguments = """{"city":"Seattle"}"""
                    }
                ]
            }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(ChatRole.Assistant, result[0].Role);

        var textContent = result[0].Contents.OfType<TextContent>().Single();
        Assert.AreEqual("Let me check.", textContent.Text);

        var functionCall = result[0].Contents.OfType<FunctionCallContent>().Single();
        Assert.AreEqual("call_1", functionCall.CallId);
        Assert.AreEqual("get_weather", functionCall.Name);
        Assert.IsTrue(functionCall.Arguments!.ContainsKey("city"));
    }

    [TestMethod]
    public void ToChatMessages_AssistantToolCalls_WithoutContent()
    {
        var messages = new List<LlmChatMessage>
        {
            new()
            {
                Role = "assistant",
                ToolCalls =
                [
                    new LlmToolCall { Id = "call_1", Name = "search", Arguments = """{"q":"test"}""" }
                ]
            }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        // No TextContent when Content is null
        Assert.AreEqual(0, result[0].Contents.OfType<TextContent>().Count());
        Assert.AreEqual(1, result[0].Contents.OfType<FunctionCallContent>().Count());
    }

    [TestMethod]
    public void ToChatMessages_NullContent_DefaultsToEmpty()
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "user" }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(string.Empty, result[0].Text);
    }

    [TestMethod]
    public void ToChatMessages_CustomRole()
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "custom_role", Content = "test" }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(new ChatRole("custom_role"), result[0].Role);
    }

    [TestMethod]
    public void ToChatMessages_MultipleMessages_PreservesOrder()
    {
        var messages = new List<LlmChatMessage>
        {
            new() { Role = "system", Content = "Be helpful." },
            new() { Role = "user", Content = "Hi" },
            new() { Role = "assistant", Content = "Hello!" },
            new() { Role = "user", Content = "Thanks" }
        };

        var result = LlmMessageMapper.ToChatMessages(messages);

        Assert.AreEqual(4, result.Count);
        Assert.AreEqual(ChatRole.System, result[0].Role);
        Assert.AreEqual(ChatRole.User, result[1].Role);
        Assert.AreEqual(ChatRole.Assistant, result[2].Role);
        Assert.AreEqual(ChatRole.User, result[3].Role);
    }

    [TestMethod]
    public void ToChatOptions_MapsRequestFields()
    {
        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }],
            ModelId = "gpt-4o",
            Temperature = 0.5f,
            MaxOutputTokens = 2048,
            StopSequences = ["END", "STOP"]
        };

        var result = LlmMessageMapper.ToChatOptions(request, new LlmOptions());

        Assert.AreEqual("gpt-4o", result.ModelId);
        Assert.AreEqual(0.5f, result.Temperature);
        Assert.AreEqual(2048, result.MaxOutputTokens);
        Assert.AreEqual(2, result.StopSequences!.Count);
        CollectionAssert.Contains(result.StopSequences.ToList(), "END");
        CollectionAssert.Contains(result.StopSequences.ToList(), "STOP");
    }

    [TestMethod]
    public void ToChatOptions_FallsBackToDefaults()
    {
        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };
        var defaults = new LlmOptions
        {
            DefaultModelId = "default-model",
            DefaultTemperature = 0.3f
        };

        var result = LlmMessageMapper.ToChatOptions(request, defaults);

        Assert.AreEqual("default-model", result.ModelId);
        Assert.AreEqual(0.3f, result.Temperature);
    }

    [TestMethod]
    public void ToChatOptions_RequestOverridesDefaults()
    {
        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }],
            ModelId = "explicit-model",
            Temperature = 0.9f
        };
        var defaults = new LlmOptions
        {
            DefaultModelId = "default-model",
            DefaultTemperature = 0.3f
        };

        var result = LlmMessageMapper.ToChatOptions(request, defaults);

        Assert.AreEqual("explicit-model", result.ModelId);
        Assert.AreEqual(0.9f, result.Temperature);
    }

    [TestMethod]
    public void ToChatOptions_MapsToolDefinitions()
    {
        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }],
            Tools =
            [
                new LlmToolDefinition
                {
                    Name = "search",
                    Description = "Search the web",
                    ParametersSchema = """{"type":"object"}"""
                }
            ]
        };

        var result = LlmMessageMapper.ToChatOptions(request, new LlmOptions());

        Assert.IsNotNull(result.Tools);
        Assert.AreEqual(1, result.Tools.Count);
        Assert.AreEqual("search", result.Tools[0].Name);
        Assert.AreEqual("Search the web", result.Tools[0].Description);
    }

    [TestMethod]
    public void ToChatOptions_NoTools_ToolsIsNull()
    {
        var request = new LlmRequest
        {
            Messages = [new LlmChatMessage { Role = "user", Content = "Hi" }]
        };

        var result = LlmMessageMapper.ToChatOptions(request, new LlmOptions());

        Assert.IsNull(result.Tools);
    }

    [TestMethod]
    public void ToLlmResponse_MapsTextResponse()
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello!"))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "gpt-4o",
            Usage = new UsageDetails { InputTokenCount = 5, OutputTokenCount = 3, TotalTokenCount = 8 }
        };

        var result = LlmMessageMapper.ToLlmResponse(chatResponse);

        Assert.AreEqual("Hello!", result.Content);
        Assert.AreEqual("stop", result.FinishReason);
        Assert.AreEqual("gpt-4o", result.ModelId);
        Assert.IsNotNull(result.Usage);
        Assert.AreEqual(5, result.Usage.InputTokens);
        Assert.AreEqual(3, result.Usage.OutputTokens);
        Assert.AreEqual(8, result.Usage.TotalTokens);
        Assert.IsNull(result.ToolCalls);
    }

    [TestMethod]
    public void ToLlmResponse_MapsToolCalls()
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call_1", "get_weather",
                new Dictionary<string, object?> { ["city"] = "Seattle" })
        });
        var chatResponse = new ChatResponse(message)
        {
            FinishReason = ChatFinishReason.ToolCalls
        };

        var result = LlmMessageMapper.ToLlmResponse(chatResponse);

        Assert.AreEqual("tool_calls", result.FinishReason);
        Assert.IsNotNull(result.ToolCalls);
        Assert.AreEqual(1, result.ToolCalls.Count);
        Assert.AreEqual("call_1", result.ToolCalls[0].Id);
        Assert.AreEqual("get_weather", result.ToolCalls[0].Name);
        Assert.IsNotNull(result.ToolCalls[0].Arguments);
        Assert.IsTrue(result.ToolCalls[0].Arguments!.Contains("Seattle"));
    }

    [TestMethod]
    public void ToLlmResponse_MapsFinishReasons()
    {
        AssertFinishReason(ChatFinishReason.Stop, "stop");
        AssertFinishReason(ChatFinishReason.Length, "length");
        AssertFinishReason(ChatFinishReason.ToolCalls, "tool_calls");
        AssertFinishReason(ChatFinishReason.ContentFilter, "content_filter");
    }

    [TestMethod]
    public void ToLlmResponse_NullFinishReason()
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"));
        var result = LlmMessageMapper.ToLlmResponse(chatResponse);
        Assert.IsNull(result.FinishReason);
    }

    [TestMethod]
    public void ToLlmResponse_NullUsage()
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hi"));
        var result = LlmMessageMapper.ToLlmResponse(chatResponse);
        Assert.IsNull(result.Usage);
    }

    [TestMethod]
    public void ToLlmResponse_EmptyMessages()
    {
        var chatResponse = new ChatResponse([]);
        var result = LlmMessageMapper.ToLlmResponse(chatResponse);
        Assert.IsNull(result.Content);
        Assert.IsNull(result.ToolCalls);
    }

    [TestMethod]
    public void ClassifyError_Timeout()
    {
        var error = LlmMessageMapper.ClassifyError(new OperationCanceledException("Timed out"));

        Assert.AreEqual(LlmError.Codes.Timeout, error.Code);
        Assert.IsTrue(error.IsRetryable);
    }

    [TestMethod]
    public void ClassifyError_TaskCanceled()
    {
        var error = LlmMessageMapper.ClassifyError(new TaskCanceledException("Timed out"));

        Assert.AreEqual(LlmError.Codes.Timeout, error.Code);
        Assert.IsTrue(error.IsRetryable);
    }

    [TestMethod]
    public void ClassifyError_HttpRateLimited()
    {
        var ex = new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests);
        var error = LlmMessageMapper.ClassifyError(ex);

        Assert.AreEqual(LlmError.Codes.RateLimited, error.Code);
        Assert.IsTrue(error.IsRetryable);
    }

    [TestMethod]
    public void ClassifyError_RateLimitInMessage()
    {
        var error = LlmMessageMapper.ClassifyError(new Exception("Rate limit exceeded"));

        Assert.AreEqual(LlmError.Codes.RateLimited, error.Code);
        Assert.IsTrue(error.IsRetryable);
    }

    [TestMethod]
    public void ClassifyError_ContextTooLong()
    {
        var error = LlmMessageMapper.ClassifyError(
            new Exception("This model's maximum context length is 128000 tokens"));

        Assert.AreEqual(LlmError.Codes.ContextTooLong, error.Code);
        Assert.IsFalse(error.IsRetryable);
    }

    [TestMethod]
    public void ClassifyError_TokenLimit()
    {
        var error = LlmMessageMapper.ClassifyError(
            new Exception("Token limit exceeded"));

        Assert.AreEqual(LlmError.Codes.ContextTooLong, error.Code);
        Assert.IsFalse(error.IsRetryable);
    }

    [TestMethod]
    public void ClassifyError_GenericException()
    {
        var error = LlmMessageMapper.ClassifyError(new Exception("Something went wrong"));

        Assert.AreEqual(LlmError.Codes.ProviderError, error.Code);
        Assert.IsFalse(error.IsRetryable);
    }

    private static void AssertFinishReason(ChatFinishReason input, string expected)
    {
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"))
        {
            FinishReason = input
        };
        var result = LlmMessageMapper.ToLlmResponse(chatResponse);
        Assert.AreEqual(expected, result.FinishReason, $"Expected '{expected}' for {input}");
    }
}
