using System.Text.Json;
using System.Text.Json.Serialization;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.ServiceSearch;

internal sealed class SearchKnownServicesExecutor(IServiceSearchIndex searchIndex) : IToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
        }
        catch
        {
            args = [];
        }

        if (!args.TryGetValue("query", out var queryEl) || queryEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = "Missing required parameter: query.",
                IsError = true
            });
        }

        var query = queryEl.GetString()!;
        var candidates = searchIndex.Search(query, maxResults: 5);

        if (candidates.Count == 0)
        {
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = JsonSerializer.Serialize(new { results = Array.Empty<object>() }, JsonOptions),
                IsError = false
            });
        }

        var results = candidates.Select(c => new SearchResultItem(
            c.Id,
            c.Type,
            c.Summary,
            c.RelevanceScore,
            c.Type == "a2a" ? c.TopItems : null,
            c.Type == "mcp" ? c.TopItems : null
        )).ToList();

        var payload = JsonSerializer.Serialize(new { results }, JsonOptions);
        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = payload,
            IsError = false
        });
    }

    private sealed record SearchResultItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore,
        [property: JsonPropertyName("top_skills")] IReadOnlyList<string>? TopSkills,
        [property: JsonPropertyName("top_tools")] IReadOnlyList<string>? TopTools
    );
}
