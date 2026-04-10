using System.Text.Json;

namespace RockBot.Tools.FileSystem;

internal sealed class FileDeleteToolExecutor(FileSystemOptions options) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Task.FromResult(Error(request, "Missing required argument: path"));

            var relativePath = pathElement.GetString() ?? string.Empty;

            var fullPath = FileWriteToolExecutor.SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Task.FromResult(Error(request, "Invalid path: must be within the shared volume."));

            if (!File.Exists(fullPath))
                return Task.FromResult(Error(request, $"File not found: {relativePath}"));

            File.Delete(fullPath);

            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = "ok",
                IsError = false
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(Error(request, $"Delete failed: {ex.Message}"));
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
