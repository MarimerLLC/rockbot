using System.Text.Json;
using Microsoft.Extensions.AI;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Wraps a <see cref="ToolRegistration"/> and its <see cref="IToolExecutor"/> as an
/// <see cref="AIFunction"/> so registry tools can be passed to the LLM in wisp LLM steps
/// with a restricted tool scope. The wisp chose these tools, so the agent loop keeps them even
/// when they are typed MCP tools and the run's tier isn't eager (<see cref="ICallerScopedTool"/>,
/// #647).
/// </summary>
internal sealed class WispRegistryToolFunction(
    ToolRegistration registration,
    IToolExecutor executor,
    string wispId,
    string? parentSessionId = null) : AIFunction, ICallerScopedTool
{
    private static readonly JsonSerializerOptions SerializerOptions = new();

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
            ToolCallId = $"wisp-{wispId}-{Guid.NewGuid():N}",
            ToolName = registration.Name,
            Arguments = argsJson,
            SessionId = parentSessionId ?? wispId
        };

        var response = await executor.ExecuteAsync(request, cancellationToken);
        return response.IsError ? $"Error: {response.Content}" : response.Content;
    }
}
