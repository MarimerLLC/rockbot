using System.Text.Json;
using Microsoft.Extensions.AI;
using RockBot.Tools;

namespace RockBot.Cli;

/// <summary>
/// Wraps a <see cref="ToolRegistration"/> and its <see cref="IToolExecutor"/> as an
/// <see cref="AIFunction"/> so registry tools (MCP, REST, scheduling, etc.) can be
/// passed directly to the LLM via <see cref="ChatOptions.Tools"/>.
/// </summary>
internal sealed class RegistryToolFunction(
    ToolRegistration registration,
    IToolExecutor executor,
    string? sessionId) : AIFunction
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
            SessionId = sessionId
        };

        var response = await executor.ExecuteAsync(request, cancellationToken);
        return response.IsError ? $"Error: {response.Content}" : response.Content;
    }
}
