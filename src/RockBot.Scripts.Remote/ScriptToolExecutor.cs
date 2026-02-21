using System.Text.Json;
using RockBot.Scripts;
using RockBot.Tools;

namespace RockBot.Scripts.Remote;

/// <summary>
/// Adapter that allows scripts to be invoked via the tool registry (tool.invoke topic).
/// Converts a <see cref="ToolInvokeRequest"/> into a script execution via <see cref="IScriptRunner"/>.
/// </summary>
internal sealed class ScriptToolExecutor(
    IScriptRunner runner,
    ScriptToolOptions options) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var args = ParseArguments(request.Arguments);

        var requestedTimeout = int.TryParse(args.GetValueOrDefault("timeout_seconds"), out var t) ? t : 30;
        var clampedTimeout = Math.Clamp(requestedTimeout, 1, options.MaxTimeoutSeconds);

        var scriptRequest = new ScriptInvokeRequest
        {
            ToolCallId = request.ToolCallId,
            Script = args.GetValueOrDefault("script") ?? throw new ArgumentException("Missing 'script' argument"),
            InputData = args.GetValueOrDefault("input_data"),
            TimeoutSeconds = clampedTimeout,
            PipPackages = args.TryGetValue("pip_packages", out var packages) && packages is not null
                ? JsonSerializer.Deserialize<List<string>>(packages)
                : null
        };

        ScriptInvokeResponse response;
        try
        {
            response = await runner.ExecuteAsync(scriptRequest, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout escaped from the runner (race condition between the internal
            // timeout and the external ct) — same treatment as MCP proxy timeouts.
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"execute_python_script timed out after {scriptRequest.TimeoutSeconds}s. " +
                          $"Either increase timeout_seconds, simplify the script, or consider " +
                          $"an alternative approach that does not require script execution.",
                IsError = true
            };
        }

        if (response.IsSuccess)
        {
            return new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = response.Output ?? "(no output)",
                IsError = false
            };
        }

        // Distinguish timeout failures from other failures so the LLM gets actionable guidance.
        var isTimeout = response.ExitCode == -1 &&
                        (response.Stderr?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true ||
                         response.Stderr?.Contains("Timed out", StringComparison.OrdinalIgnoreCase) == true);

        var errorContent = isTimeout
            ? $"execute_python_script timed out after {scriptRequest.TimeoutSeconds}s. " +
              $"Either increase timeout_seconds, simplify the script, or consider " +
              $"an alternative approach that does not require script execution."
            : $"Script failed (exit {response.ExitCode}):\n{response.Output ?? response.Stderr ?? "(no output)"}";

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = errorContent,
            IsError = true
        };
    }

    private static Dictionary<string, string?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        if (dict is null)
            return [];

        return dict.ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => kv.Value.GetString(),
            _ => kv.Value.GetRawText()
        });
    }
}
