using System.Text.Json;
using Microsoft.Extensions.AI;

namespace RockBot.Tools;

/// <summary>
/// Wraps a <see cref="ToolRegistration"/> and its <see cref="IToolExecutor"/> as an
/// <see cref="AIFunction"/> so registry tools (MCP, REST, scheduling, etc.) can be
/// passed directly to the LLM via <see cref="ChatOptions.Tools"/>.
/// </summary>
public sealed class RegistryToolFunction(
    ToolRegistration registration,
    IToolExecutor executor,
    string? sessionId,
    string? batchId = null,
    Action<string>? onInvoke = null) : AIFunction
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

    /// <summary>
    /// Minimal valid OpenAI tool schema used as a fallback when a tool has no schema
    /// or an unparseable one. LM Studio's grammar compiler requires at minimum
    /// <c>{"type":"object","properties":{}}</c>.
    /// </summary>
    private static readonly JsonElement FallbackSchema =
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement;

    public override string Name => registration.Name;
    public override string Description => registration.Description;

    public override JsonElement JsonSchema
    {
        get
        {
            if (string.IsNullOrEmpty(registration.ParametersSchema)) return FallbackSchema;
            try { return JsonDocument.Parse(registration.ParametersSchema).RootElement; }
            catch { return FallbackSchema; }
        }
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        string? argsJson = null;
        if (arguments is { Count: > 0 })
        {
            argsJson = JsonSerializer.Serialize(
                arguments.ToDictionary(k => k.Key, k => k.Value),
                SerializerOptions);
        }

        var request = new ToolInvokeRequest
        {
            ToolCallId = Guid.NewGuid().ToString("N"),
            ToolName = registration.Name,
            Arguments = argsJson,
            SessionId = sessionId,
            BatchId = batchId
        };

        onInvoke?.Invoke(registration.Name);

        var response = await executor.ExecuteAsync(request, cancellationToken);

        // For errors, always return plain text so the LLM receives a clear error message.
        if (response.IsError)
            return $"Error: {response.Content}";

        // When the result contains non-text blocks (images, audio, etc.), return a list of
        // AIContent items so the LLM provider can serialize them as multimodal content blocks
        // rather than losing the data.
        if (response.ContentBlocks is { Count: > 0 }
            && response.ContentBlocks.Any(b => b.Type != "text"))
        {
            var aiContents = response.ContentBlocks
                .Select(ToAIContent)
                .OfType<AIContent>()
                .ToList();

            if (aiContents.Count > 0)
                return aiContents;
        }

        return response.Content;
    }

    private static AIContent? ToAIContent(ToolContentBlock block) => block.Type switch
    {
        "text" => new TextContent(block.Text ?? string.Empty),
        "image" when block.Data is not null && block.MimeType is not null
            => new DataContent($"data:{block.MimeType};base64,{block.Data}", block.MimeType),
        "audio" when block.Data is not null && block.MimeType is not null
            => new DataContent($"data:{block.MimeType};base64,{block.Data}", block.MimeType),
        _ => block.Text is not null ? new TextContent(block.Text) : null
    };
}
