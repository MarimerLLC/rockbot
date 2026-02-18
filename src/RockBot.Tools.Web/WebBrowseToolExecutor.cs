using System.Text.Json;

namespace RockBot.Tools.Web;

internal sealed class WebBrowseToolExecutor(IWebBrowseProvider browseProvider) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string url;

        try
        {
            var args = ParseArguments(request.Arguments);
            if (!args.TryGetValue("url", out var urlElement))
                return Error(request, "Missing required argument: url");

            url = urlElement.GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return Error(request, $"Invalid arguments: {ex.Message}");
        }

        try
        {
            var page = await browseProvider.FetchAsync(url, ct);

            var content = string.IsNullOrWhiteSpace(page.Title)
                ? page.Content
                : $"# {page.Title}\n\n{page.Content}";

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = content,
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Failed to fetch page: {ex.Message}");
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
