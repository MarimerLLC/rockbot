using System.Text.Json;

namespace RockBot.Tools.FileSystem;

internal sealed class FileReadToolExecutor(FileSystemOptions options) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Error(request, "Missing required argument: path");

            var relativePath = pathElement.GetString() ?? string.Empty;

            var fullPath = FileWriteToolExecutor.SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            if (!File.Exists(fullPath))
                return Error(request, $"File not found: {relativePath}");

            var content = await File.ReadAllTextAsync(fullPath, ct);

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
            return Error(request, $"Read failed: {ex.Message}");
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
