using System.Text.Json;
using Microsoft.Extensions.AI;
using RockBot.Tools;

namespace RockBot.ResearchAgent;

/// <summary>
/// Wraps a <see cref="ToolRegistration"/> and its <see cref="IToolExecutor"/> as an
/// <see cref="AIFunction"/> for use in the research agent loop.
/// Mirrors <c>RegistryToolFunction</c> in <c>RockBot.A2A</c> (which is internal to that assembly).
/// </summary>
internal sealed class ResearchToolFunction(
    ToolRegistration registration,
    IToolExecutor executor,
    string? sessionId) : AIFunction
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
            ToolCallId = Guid.NewGuid().ToString("N"),
            ToolName = registration.Name,
            Arguments = argsJson,
            SessionId = sessionId
        };

        var response = await executor.ExecuteAsync(request, cancellationToken);
        return response.IsError ? $"Error: {response.Content}" : response.Content;
    }
}
