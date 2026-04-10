using System.Text.Json;

namespace RockBot.Tools.FileSystem;

internal sealed class FileListToolExecutor(FileSystemOptions options) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            var prefix = args.TryGetValue("prefix", out var prefixElement)
                ? prefixElement.GetString() ?? ""
                : "";

            var basePath = Path.GetFullPath(options.BasePath);
            var searchPath = string.IsNullOrEmpty(prefix)
                ? basePath
                : Path.GetFullPath(Path.Combine(basePath, prefix));

            if (!searchPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Error(request, "Invalid prefix: must be within the shared volume."));
            }

            if (!Directory.Exists(searchPath))
            {
                return Task.FromResult(new ToolInvokeResponse
                {
                    ToolCallId = request.ToolCallId,
                    ToolName = request.ToolName,
                    Content = "[]",
                    IsError = false
                });
            }

            var files = Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(basePath, f).Replace('\\', '/'))
                .Order()
                .ToList();

            var json = JsonSerializer.Serialize(files);

            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = json,
                IsError = false
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(request, $"List failed: {ex.Message}"));
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
