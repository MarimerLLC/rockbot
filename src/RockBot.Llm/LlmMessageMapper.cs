using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RockBot.Llm;

/// <summary>
/// Maps between RockBot LLM message types and Microsoft.Extensions.AI types.
/// </summary>
internal static class LlmMessageMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Maps LlmChatMessages to M.E.AI ChatMessages.
    /// </summary>
    public static IList<ChatMessage> ToChatMessages(IReadOnlyList<LlmChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);

        foreach (var msg in messages)
        {
            var role = ToChatRole(msg.Role);

            if (role == ChatRole.Tool && msg.ToolCallId is not null)
            {
                // Tool result message
                var contents = new List<AIContent>
                {
                    new FunctionResultContent(msg.ToolCallId, msg.Content)
                };
                result.Add(new ChatMessage(role, contents));
            }
            else if (msg.ToolCalls is { Count: > 0 })
            {
                // Message with tool calls (typically assistant)
                var contents = new List<AIContent>();
                if (msg.Content is not null)
                    contents.Add(new TextContent(msg.Content));

                foreach (var tc in msg.ToolCalls)
                {
                    var arguments = tc.Arguments is not null
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.Arguments, JsonOptions)
                          ?? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>();

                    contents.Add(new FunctionCallContent(tc.Id, tc.Name, arguments));
                }

                result.Add(new ChatMessage(role, contents));
            }
            else
            {
                // Simple text message
                result.Add(new ChatMessage(role, msg.Content ?? string.Empty));
            }
        }

        return result;
    }

    /// <summary>
    /// Maps an LlmRequest to M.E.AI ChatOptions.
    /// </summary>
    public static ChatOptions ToChatOptions(LlmRequest request, LlmOptions defaults)
    {
        var options = new ChatOptions
        {
            ModelId = request.ModelId ?? defaults.DefaultModelId,
            Temperature = request.Temperature ?? defaults.DefaultTemperature,
            MaxOutputTokens = request.MaxOutputTokens,
            StopSequences = request.StopSequences?.ToList()
        };

        if (request.Tools is { Count: > 0 })
            options.Tools = request.Tools.Select(ToAITool).ToList<AITool>();

        return options;
    }

    /// <summary>
    /// Maps a ChatResponse to an LlmResponse.
    /// </summary>
    public static LlmResponse ToLlmResponse(ChatResponse response)
    {
        var message = response.Messages.Count > 0 ? response.Messages[^1] : null;

        var toolCalls = message?.Contents
            .OfType<FunctionCallContent>()
            .Select(fc => new LlmToolCall
            {
                Id = fc.CallId,
                Name = fc.Name,
                Arguments = fc.Arguments is { Count: > 0 }
                    ? JsonSerializer.Serialize(fc.Arguments, JsonOptions)
                    : null
            })
            .ToList();

        return new LlmResponse
        {
            Content = message?.Text,
            ToolCalls = toolCalls is { Count: > 0 } ? toolCalls : null,
            FinishReason = FromFinishReason(response.FinishReason),
            Usage = response.Usage is { } u
                ? new LlmUsage
                {
                    InputTokens = (int)(u.InputTokenCount ?? 0),
                    OutputTokens = (int)(u.OutputTokenCount ?? 0),
                    TotalTokens = (int)(u.TotalTokenCount ?? 0)
                }
                : null,
            ModelId = response.ModelId
        };
    }

    /// <summary>
    /// Classifies a provider exception into an LlmError.
    /// </summary>
    public static LlmError ClassifyError(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return new LlmError
            {
                Code = LlmError.Codes.Timeout,
                Message = ex.Message,
                IsRetryable = true
            };
        }

        if (ex is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new LlmError
            {
                Code = LlmError.Codes.RateLimited,
                Message = ex.Message,
                IsRetryable = true
            };
        }

        var msg = ex.Message;

        if (ContainsAny(msg, "rate limit", "rate_limit"))
        {
            return new LlmError
            {
                Code = LlmError.Codes.RateLimited,
                Message = msg,
                IsRetryable = true
            };
        }

        if (ContainsAny(msg, "context length", "context_length", "token limit", "too long"))
        {
            return new LlmError
            {
                Code = LlmError.Codes.ContextTooLong,
                Message = msg,
                IsRetryable = false
            };
        }

        return new LlmError
        {
            Code = LlmError.Codes.ProviderError,
            Message = msg,
            IsRetryable = false
        };
    }

    private static ChatRole ToChatRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => new ChatRole(role)
    };

    private static string? FromFinishReason(ChatFinishReason? reason)
    {
        if (reason is null) return null;
        var r = reason.Value;
        if (r == ChatFinishReason.Stop) return "stop";
        if (r == ChatFinishReason.Length) return "length";
        if (r == ChatFinishReason.ToolCalls) return "tool_calls";
        if (r == ChatFinishReason.ContentFilter) return "content_filter";
        return r.Value;
    }

    private static AITool ToAITool(LlmToolDefinition definition) =>
        new ToolDefinitionWrapper(definition.Name, definition.Description, definition.ParametersSchema);

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(v => text.Contains(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Minimal AITool implementation for passing tool definitions to IChatClient.
    /// The provider is responsible for converting this to its native format.
    /// </summary>
    private sealed class ToolDefinitionWrapper(string name, string description, string? parametersSchema) : AITool
    {
        public override string Name => name;
        public override string Description => description;

        /// <summary>
        /// The raw JSON Schema for the tool's parameters, if any.
        /// Providers that support function calling can read this from AdditionalProperties.
        /// </summary>
        public override IReadOnlyDictionary<string, object?> AdditionalProperties { get; } =
            parametersSchema is not null
                ? new Dictionary<string, object?> { ["parametersSchema"] = parametersSchema }
                : new Dictionary<string, object?>();
    }
}
