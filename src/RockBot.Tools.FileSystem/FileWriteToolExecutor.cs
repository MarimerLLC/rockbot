using System.Text.Json;

namespace RockBot.Tools.FileSystem;

internal sealed class FileWriteToolExecutor(FileSystemOptions options) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Error(request, "Missing required argument: path");
            if (!args.TryGetValue("content", out var contentElement))
                return Error(request, "Missing required argument: content");

            var relativePath = pathElement.GetString() ?? string.Empty;
            var content = contentElement.GetString() ?? string.Empty;

            var fullPath = SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content, ct);

            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"Written {content.Length} characters to {relativePath}",
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Write failed: {ex.Message}");
        }
    }

    internal static string? SafeResolvePath(string basePath, string relativePath)
    {
        var fullBase = Path.GetFullPath(basePath);
        var fullPath = Path.GetFullPath(Path.Combine(fullBase, relativePath));
        return fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
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
