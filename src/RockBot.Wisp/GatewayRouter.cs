using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Maps wisp step definitions to concrete tool invocations via the tool registry.
/// Each gateway type routes to a specific registered tool with appropriately formatted arguments.
/// </summary>
internal static class GatewayRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Resolves a direct step into a <see cref="ToolInvokeRequest"/> by mapping
    /// the gateway type to the correct registered tool and building its arguments.
    /// </summary>
    public static ToolRouteResult Route(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        return step.Gateway switch
        {
            GatewayType.Mcp => RouteMcp(step, wispId, priorResults),
            GatewayType.A2A => RouteA2A(step, wispId, priorResults),
            GatewayType.Script => RouteScript(step, wispId, priorResults),
            GatewayType.Web => RouteWeb(step, wispId, priorResults),
            null => ToolRouteResult.Failure("Gateway is required for direct mode steps", FailureCategory.Structural),
            _ => ToolRouteResult.Failure($"Unknown gateway type: {step.Gateway}", FailureCategory.Structural)
        };
    }

    /// <summary>
    /// Returns the registered tool name that a gateway type maps to.
    /// Used for tool scope resolution.
    /// </summary>
    public static string? GetToolName(WispStep step)
    {
        return step.Gateway switch
        {
            GatewayType.Mcp => "mcp_invoke_tool",
            GatewayType.A2A => "invoke_agent",
            GatewayType.Script => $"execute_{step.Language ?? "python"}_script",
            GatewayType.Web => step.Tool,
            _ => null
        };
    }

    private static ToolRouteResult RouteMcp(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        if (string.IsNullOrEmpty(step.Server))
            return ToolRouteResult.Failure("MCP gateway requires 'server' field", FailureCategory.Structural);
        if (string.IsNullOrEmpty(step.Tool))
            return ToolRouteResult.Failure("MCP gateway requires 'tool' field", FailureCategory.Structural);

        var resolvedParams = ResolveTemplates(step.Params, priorResults);

        var args = new Dictionary<string, object?>
        {
            ["server_name"] = step.Server,
            ["tool_name"] = step.Tool
        };

        if (resolvedParams is not null)
            args["arguments"] = resolvedParams;

        return ToolRouteResult.Success("mcp_invoke_tool", JsonSerializer.Serialize(args, JsonOptions));
    }

    private static ToolRouteResult RouteA2A(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        if (string.IsNullOrEmpty(step.Agent))
            return ToolRouteResult.Failure("A2A gateway requires 'agent' field", FailureCategory.Structural);
        if (string.IsNullOrEmpty(step.Skill))
            return ToolRouteResult.Failure("A2A gateway requires 'skill' field", FailureCategory.Structural);
        if (string.IsNullOrEmpty(step.Message))
            return ToolRouteResult.Failure("A2A gateway requires 'message' field", FailureCategory.Structural);

        var message = ResolveTemplateString(step.Message, priorResults);

        var args = new Dictionary<string, object?>
        {
            ["agent_name"] = step.Agent,
            ["skill"] = step.Skill,
            ["message"] = message
        };

        if (step.TimeoutMinutes is not null)
            args["timeout_minutes"] = step.TimeoutMinutes;

        return ToolRouteResult.Success("invoke_agent", JsonSerializer.Serialize(args, JsonOptions));
    }

    private static ToolRouteResult RouteScript(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        if (step.Params is null)
            return ToolRouteResult.Failure("Script gateway requires 'params' with 'script' field", FailureCategory.Structural);

        var paramsObj = step.Params.Value;
        if (!paramsObj.TryGetProperty("script", out _))
            return ToolRouteResult.Failure("Script gateway requires 'script' in params", FailureCategory.Structural);

        var resolvedParams = ResolveTemplates(step.Params, priorResults);
        var language = step.Language ?? "python";
        var toolName = $"execute_{language}_script";

        return ToolRouteResult.Success(toolName,
            resolvedParams is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(resolvedParams, JsonOptions));
    }

    private static ToolRouteResult RouteWeb(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        if (string.IsNullOrEmpty(step.Tool))
            return ToolRouteResult.Failure("Web gateway requires 'tool' field (web_search or web_browse)", FailureCategory.Structural);

        if (step.Tool is not ("web_search" or "web_browse"))
            return ToolRouteResult.Failure($"Web gateway tool must be 'web_search' or 'web_browse', got '{step.Tool}'", FailureCategory.Structural);

        var resolvedParams = ResolveTemplates(step.Params, priorResults);

        return ToolRouteResult.Success(step.Tool,
            resolvedParams is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(resolvedParams ?? new object(), JsonOptions));
    }

    /// <summary>
    /// Resolves <c>{{steps.id.result}}</c> and <c>{{steps.id.output_to}}</c> template references
    /// in a JSON params element by replacing them with actual values from prior step results.
    /// </summary>
    private static object? ResolveTemplates(JsonElement? paramsElement, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        if (paramsElement is null)
            return null;

        var raw = paramsElement.Value.GetRawText();
        var resolved = ResolveTemplateString(raw, priorResults);
        return JsonSerializer.Deserialize<JsonElement>(resolved);
    }

    internal static string ResolveTemplateString(string input, IReadOnlyDictionary<string, WispStepResult> priorResults)
    {
        // Replace {{steps.<id>.result}} with the step's content
        // Replace {{steps.<id>.output_to}} with the step's output_to path (looked up from definition, not results)
        var result = input;

        foreach (var (stepId, stepResult) in priorResults)
        {
            var resultPlaceholder = $"{{{{steps.{stepId}.result}}}}";
            if (result.Contains(resultPlaceholder))
            {
                var escaped = JsonEscapeForEmbedding(stepResult.Content ?? "");
                result = result.Replace(resultPlaceholder, escaped);
            }
        }

        return result;
    }

    /// <summary>
    /// Escapes a string value for embedding within an already-quoted JSON string.
    /// Handles newlines, quotes, backslashes, and other control characters.
    /// </summary>
    private static string JsonEscapeForEmbedding(string value)
    {
        // Use JsonSerializer to get properly escaped content, then strip the surrounding quotes
        var serialized = JsonSerializer.Serialize(value);
        return serialized[1..^1]; // strip leading and trailing quote
    }
}

/// <summary>
/// Result of routing a wisp step to a tool invocation.
/// </summary>
internal sealed record ToolRouteResult
{
    public bool IsSuccess { get; private init; }
    public string? ToolName { get; private init; }
    public string? Arguments { get; private init; }
    public string? ErrorMessage { get; private init; }
    public FailureCategory? ErrorCategory { get; private init; }

    public static ToolRouteResult Success(string toolName, string arguments) => new()
    {
        IsSuccess = true,
        ToolName = toolName,
        Arguments = arguments
    };

    public static ToolRouteResult Failure(string message, FailureCategory category) => new()
    {
        IsSuccess = false,
        ErrorMessage = message,
        ErrorCategory = category
    };
}
