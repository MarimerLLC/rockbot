using System.Text;
using System.Text.Json;

namespace RockBot.Tools.Web;

internal sealed class WebSearchToolExecutor(
    IWebSearchProvider searchProvider,
    WebToolOptions options) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string query;
        int count = options.MaxSearchResults;

        try
        {
            var args = ParseArguments(request.Arguments);
            if (!args.TryGetValue("query", out var queryElement))
            {
                return Error(request, "Missing required argument: query");
            }

            query = queryElement.GetString() ?? string.Empty;

            if (args.TryGetValue("count", out var countElement) && countElement.ValueKind == JsonValueKind.Number)
                count = Math.Min(countElement.GetInt32(), 20);
        }
        catch (Exception ex)
        {
            return Error(request, $"Invalid arguments: {ex.Message}");
        }

        try
        {
            var results = await searchProvider.SearchAsync(query, count, ct);

            if (results.Count == 0)
            {
                return new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = "No results found.",
                    IsError = false
                };
            }

            var sb = new StringBuilder();
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.AppendLine($"{i + 1}. [{r.Title}]({r.Url})");
                if (!string.IsNullOrWhiteSpace(r.Snippet))
                    sb.AppendLine($"   {r.Snippet}");
            }

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = sb.ToString().TrimEnd(),
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Search failed: {ex.Message}");
        }
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) =>
        new()
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = message,
            IsError = true
        };

    private static Dictionary<string, JsonElement> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
    }
}
