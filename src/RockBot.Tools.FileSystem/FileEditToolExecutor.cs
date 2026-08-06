using System.Text.Json;
using RockBot.Host;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// Applies an exact-match replacement to a single file on the shared volume,
/// leaving the rest of the file byte-for-byte untouched.
/// </summary>
internal sealed class FileEditToolExecutor(FileSystemOptions options) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        try
        {
            var args = ParseArguments(request.Arguments);

            if (!args.TryGetValue("path", out var pathElement))
                return Error(request, "Missing required argument: path");
            if (!args.TryGetValue("old_string", out var oldElement))
                return Error(request, "Missing required argument: old_string");
            if (!args.TryGetValue("new_string", out var newElement))
                return Error(request, "Missing required argument: new_string");

            var relativePath = pathElement.GetString() ?? string.Empty;
            var oldString = oldElement.GetString() ?? string.Empty;
            var newString = newElement.GetString() ?? string.Empty;
            var replaceAll = args.TryGetValue("replace_all", out var replaceAllElement)
                && replaceAllElement.ValueKind == JsonValueKind.True;

            var fullPath = FileWriteToolExecutor.SafeResolvePath(options.BasePath, relativePath);
            if (fullPath is null)
                return Error(request, "Invalid path: must be within the shared volume.");

            if (!File.Exists(fullPath))
                return Error(request, $"File not found: {relativePath}. Use file_write to create it.");

            var original = await File.ReadAllTextAsync(fullPath, ct);
            var result = TextEdit.Apply(original, oldString, newString, replaceAll);

            if (!result.IsSuccess)
                return Error(request, $"Edit failed on {relativePath}: {result.Error}");

            await File.WriteAllTextAsync(fullPath, result.Content!, ct);

            var plural = result.ReplacementCount == 1 ? "occurrence" : "occurrences";
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"Replaced {result.ReplacementCount} {plural} in {relativePath} "
                    + $"({original.Length} → {result.Content!.Length} characters).",
                IsError = false
            };
        }
        catch (Exception ex)
        {
            return Error(request, $"Edit failed: {ex.Message}");
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
