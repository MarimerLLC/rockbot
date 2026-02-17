using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Delegate that invokes an MCP tool and returns the result.
/// </summary>
internal delegate ValueTask<CallToolResult> CallToolDelegate(
    IReadOnlyDictionary<string, object?>? arguments,
    CancellationToken ct);

/// <summary>
/// Executes a tool invocation by calling an MCP server tool.
/// </summary>
internal sealed class McpToolExecutor(CallToolDelegate callTool) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        var arguments = ParseArguments(request.Arguments);

        var result = await callTool(arguments, ct);

        var content = FormatResult(result);

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = content,
            IsError = result.IsError == true
        };
    }

    internal static Dictionary<string, object?> ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? [];
    }

    internal static string? FormatResult(CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
            return null;

        var textParts = result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text);

        var joined = string.Join("\n", textParts);
        return string.IsNullOrEmpty(joined) ? null : joined;
    }
}
