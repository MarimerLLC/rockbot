using System.Text.Json;
using Microsoft.Extensions.AI;
using RockBot.Tools;

namespace RockBot.ResearchAgent;

/// <summary>
/// Wraps a <see cref="ToolRegistration"/> and its <see cref="IToolExecutor"/> as an
/// <see cref="AIFunction"/> for use in the research agent loop.
/// Mirrors <c>RegistryToolFunction</c> in <c>RockBot.A2A</c> (which is internal to that assembly).
/// A successful <c>web_browse</c> is recorded in <paramref name="sources"/>, when one is given.
/// </summary>
internal sealed class ResearchToolFunction(
    ToolRegistration registration,
    IToolExecutor executor,
    string? sessionId,
    SourceLog? sources = null) : AIFunction
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
        if (sources is not null && !response.IsError && registration.Name == "web_browse"
            && arguments?.TryGetValue("url", out var url) == true
            && !(response.Content ?? "").Contains("[web_browse status:", StringComparison.Ordinal))
        {
            sources.Add(url switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
                _ => url?.ToString() ?? "",
            }, SourceLog.TitleFrom(response.Content));
        }
        return response.IsError ? $"Error: {response.Content}" : response.Content;
    }
}
