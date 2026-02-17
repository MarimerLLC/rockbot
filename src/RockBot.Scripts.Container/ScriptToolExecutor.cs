using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Scripts.Container;

/// <summary>
/// Adapter that allows scripts to be invoked via the tool registry (tool.invoke topic).
/// Converts a ToolInvokeRequest into a direct script execution.
/// </summary>
internal sealed class ScriptToolExecutor(
    ContainerScriptRunner runner) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);

        var scriptRequest = new ScriptInvokeRequest
        {
            ToolCallId = request.ToolCallId,
            Script = args.GetValueOrDefault("script") ?? throw new ArgumentException("Missing 'script' argument"),
            InputData = args.GetValueOrDefault("input_data"),
            TimeoutSeconds = int.TryParse(args.GetValueOrDefault("timeout_seconds"), out var t) ? t : 30,
            PipPackages = args.TryGetValue("pip_packages", out var packages) && packages is not null
                ? JsonSerializer.Deserialize<List<string>>(packages)
                : null
        };

        var response = await runner.ExecuteAsync(scriptRequest, ct);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = response.IsSuccess ? response.Output : $"Error (exit {response.ExitCode}): {response.Stderr ?? response.Output}",
            IsError = !response.IsSuccess
        };
    }

    private static Dictionary<string, string?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        if (dict is null)
            return [];

        return dict.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind == JsonValueKind.Null ? null : kv.Value.GetRawText().Trim('"'));
    }
}
