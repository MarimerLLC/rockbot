using System.Text.Json;
using System.Text.RegularExpressions;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Maps wisp step definitions to concrete tool invocations via the tool registry.
/// Each gateway type routes to a specific registered tool with appropriately formatted arguments.
/// </summary>
internal static partial class GatewayRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Resolves a direct step into a <see cref="ToolInvokeRequest"/> by mapping
    /// the gateway type to the correct registered tool and building its arguments.
    /// </summary>
    /// <param name="typed">
    /// For an MCP step whose tool has a typed wrapper (#647): the resolved tool. The step is then
    /// routed to the wrapper with the tool's own arguments; without it, to <c>mcp_invoke_tool</c>.
    /// </param>
    public static ToolRouteResult Route(
        WispStep step,
        string wispId,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        McpToolEntry? typed = null,
        ForEachItem? item = null)
    {
        return step.Gateway switch
        {
            GatewayType.Mcp => RouteMcp(step, wispId, priorResults, typed, item),
            GatewayType.A2A => RouteA2A(step, wispId, priorResults, item),
            GatewayType.Script => RouteScript(step, wispId, priorResults, item),
            GatewayType.Web => RouteWeb(step, wispId, priorResults, item),
            null => ToolRouteResult.Failure("Gateway is required for direct mode steps", FailureCategory.Structural),
            _ => ToolRouteResult.Failure($"Unknown gateway type: {step.Gateway}", FailureCategory.Structural)
        };
    }

    /// <summary>
    /// Returns the registered tool name that a gateway type maps to.
    /// Used for tool scope resolution. For MCP this is the generic fallback; a step whose tool
    /// has a typed wrapper is scoped to the wrapper instead (<c>WispExecutor.BuildLlmStepTools</c>).
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

    private static ToolRouteResult RouteMcp(
        WispStep step,
        string wispId,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        McpToolEntry? typed,
        ForEachItem? item)
    {
        if (string.IsNullOrEmpty(step.Tool))
            return ToolRouteResult.Failure("MCP gateway requires 'tool' field", FailureCategory.Structural);
        if (string.IsNullOrEmpty(step.Server) && typed?.Wrapper is null)
            return ToolRouteResult.Failure(
                "MCP gateway requires 'server' field, or a typed tool name (server__tool) from your tool list in 'tool'",
                FailureCategory.Structural);

        // Missing params is treated as an empty arguments object. The MCP server validates
        // its own tool schema and returns a specific error if required arguments are absent.
        var resolvedParams = ResolveTemplates(step.ResolvedParams, priorResults, item);

        // Typed wrapper: called by its own name with the tool's own arguments (#647).
        if (typed?.Wrapper is { } wrapper)
            return ToolRouteResult.Success(wrapper.Name,
                resolvedParams is null ? "{}" : JsonSerializer.Serialize(resolvedParams, JsonOptions));

        var args = new Dictionary<string, object?>
        {
            ["server_name"] = step.Server,
            ["tool_name"] = step.Tool,
            ["arguments"] = resolvedParams ?? JsonDocument.Parse("{}").RootElement
        };

        return ToolRouteResult.Success("mcp_invoke_tool", JsonSerializer.Serialize(args, JsonOptions));
    }

    private static ToolRouteResult RouteA2A(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults, ForEachItem? item)
    {
        if (string.IsNullOrEmpty(step.Agent))
            return ToolRouteResult.Failure("A2A gateway requires 'agent' field", FailureCategory.Structural);
        if (string.IsNullOrEmpty(step.Skill))
            return ToolRouteResult.Failure("A2A gateway requires 'skill' field", FailureCategory.Structural);
        if (string.IsNullOrEmpty(step.Message))
            return ToolRouteResult.Failure("A2A gateway requires 'message' field", FailureCategory.Structural);

        var message = ResolveTemplateString(step.Message, priorResults, definition: null, item);

        var args = new Dictionary<string, object?>
        {
            ["agent_name"] = step.Agent,
            ["skill"] = step.Skill,
            ["message"] = message
        };

        if (step.TimeoutMinutes is not null)
            args["timeout_minutes"] = step.TimeoutMinutes;

        if (step.Metadata is JsonElement md && md.ValueKind == JsonValueKind.Object)
            args["metadata"] = md;

        return ToolRouteResult.Success("invoke_agent", JsonSerializer.Serialize(args, JsonOptions));
    }

    private static ToolRouteResult RouteScript(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults, ForEachItem? item)
    {
        if (step.ResolvedParams is null)
            return ToolRouteResult.Failure("Script gateway requires 'params' with 'script' field", FailureCategory.Structural);

        var paramsObj = step.ResolvedParams.Value;
        if (!paramsObj.TryGetProperty("script", out _))
            return ToolRouteResult.Failure("Script gateway requires 'script' in params", FailureCategory.Structural);

        var resolvedParams = ResolveTemplates(step.ResolvedParams, priorResults, item);
        var language = step.Language ?? "python";
        var toolName = $"execute_{language}_script";

        return ToolRouteResult.Success(toolName,
            resolvedParams is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(resolvedParams, JsonOptions));
    }

    private static ToolRouteResult RouteWeb(WispStep step, string wispId, IReadOnlyDictionary<string, WispStepResult> priorResults, ForEachItem? item)
    {
        if (string.IsNullOrEmpty(step.Tool))
            return ToolRouteResult.Failure("Web gateway requires 'tool' field (web_search or web_browse)", FailureCategory.Structural);

        if (step.Tool is not ("web_search" or "web_browse"))
            return ToolRouteResult.Failure($"Web gateway tool must be 'web_search' or 'web_browse', got '{step.Tool}'", FailureCategory.Structural);

        var resolvedParams = ResolveTemplates(step.ResolvedParams, priorResults, item);

        return ToolRouteResult.Success(step.Tool,
            resolvedParams is JsonElement je ? je.GetRawText() : JsonSerializer.Serialize(resolvedParams ?? new object(), JsonOptions));
    }

    /// <summary>
    /// Resolves <c>{{steps.id.result}}</c> and <c>{{steps.id.output_to}}</c> template references
    /// in a JSON params element by replacing them with actual values from prior step results.
    /// </summary>
    private static object? ResolveTemplates(
        JsonElement? paramsElement, IReadOnlyDictionary<string, WispStepResult> priorResults, ForEachItem? item)
    {
        if (paramsElement is null)
            return null;

        var raw = paramsElement.Value.GetRawText();
        var resolved = ResolveTemplateString(raw, priorResults, definition: null, item);
        return JsonSerializer.Deserialize<JsonElement>(resolved);
    }

    /// <summary>
    /// Resolves template references in the input string. Supports:
    /// <c>{{steps.id.result}}</c> — replaced with step's output content.
    /// </summary>
    internal static string ResolveTemplateString(string input, IReadOnlyDictionary<string, WispStepResult> priorResults)
        => ResolveTemplateString(input, priorResults, definition: null);

    /// <summary>
    /// Resolves template references in the input string. Supports:
    /// <c>{{steps.id.result}}</c> — replaced with the step's full output content.
    /// <c>{{steps.id.result.a.b.c}}</c> — replaced with the value at the dotted path
    ///   within the step's output parsed as JSON. A string value is inserted as its
    ///   string value (quotes stripped); non-string values are inserted as their JSON
    ///   representation. If the step's content isn't JSON or the path doesn't resolve,
    ///   the literal template is left in place — downstream validation/soft-error
    ///   detection surfaces the failure.
    /// <c>{{steps.id.output_to}}</c> — replaced with the step's output_to file path
    ///   (from the wisp definition).
    /// </summary>
    internal static string ResolveTemplateString(
        string input,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        WispDefinition? definition)
        => ResolveTemplateString(input, priorResults, definition, item: null);

    /// <summary>
    /// As <see cref="ResolveTemplateString(string, IReadOnlyDictionary{string, WispStepResult}, WispDefinition?)"/>,
    /// and for one element of a <c>for_each</c> step (#779) also:
    /// <c>{{item}}</c> / <c>{{item.a.b}}</c> (and the same with the step's <c>as</c> alias) — the
    ///   current element, or the value at a dotted path inside it, inserted with the same rules as a
    ///   step result path; <c>{{item_index}}</c> — the element's zero-based position.
    /// Step and item templates are resolved in a single pass, so text substituted from one source
    /// is never re-scanned as a template.
    /// </summary>
    internal static string ResolveTemplateString(
        string input,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        WispDefinition? definition,
        ForEachItem? item)
    {
        // Match {{steps.<stepId>.result[.<dot.path>]}} and, when iterating, {{<name>[.<dot.path>]}}.
        var result = TemplatePattern().Replace(input, match =>
        {
            if (match.Groups["step"].Success)
            {
                var stepId = match.Groups["step"].Value;
                var path = match.Groups["spath"].Success ? match.Groups["spath"].Value : null;

                if (!priorResults.TryGetValue(stepId, out var stepResult))
                    return match.Value;

                var content = stepResult.Content ?? "";

                if (path is null)
                    return JsonEscapeForEmbedding(content);

                // Path present — parse as JSON and navigate.
                if (!TryNavigateJsonPath(content, path, out var extracted))
                    return match.Value; // not JSON, or path not found → leave literal

                return JsonEscapeForEmbedding(extracted);
            }

            if (item is null)
                return match.Value;

            var name = match.Groups["name"].Value;
            var itemPath = match.Groups["ipath"].Success ? match.Groups["ipath"].Value : null;

            if (string.Equals(name, ItemIndexName, StringComparison.Ordinal))
                return itemPath is null ? item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : match.Value;

            if (!item.Matches(name))
                return match.Value;

            if (!TryNavigate(item.Value, itemPath, out var element))
                return match.Value; // path not found → leave literal, as for a step result

            return JsonEscapeForEmbedding(ElementText(element));
        });

        // Resolve {{steps.id.output_to}} from the definition's step declarations
        if (definition is not null)
        {
            foreach (var step in definition.Steps)
            {
                var outputToPlaceholder = $"{{{{steps.{step.Id}.output_to}}}}";
                if (result.Contains(outputToPlaceholder) && !string.IsNullOrEmpty(step.OutputTo))
                {
                    var escaped = JsonEscapeForEmbedding(step.OutputTo);
                    result = result.Replace(outputToPlaceholder, escaped);
                }
            }
        }

        return result;
    }

    /// <summary>The template name for a <c>for_each</c> element (#779).</summary>
    internal const string ItemName = "item";

    /// <summary>The template name for a <c>for_each</c> element's zero-based position (#779).</summary>
    internal const string ItemIndexName = "item_index";

    /// <summary>
    /// Parses a <c>for_each</c> source (#779): exactly one <c>{{steps.&lt;id&gt;.result[.path]}}</c>
    /// template, optionally surrounded by whitespace.
    /// </summary>
    internal static bool TryParseForEachSource(string? forEach, out string stepId, out string? path)
    {
        stepId = "";
        path = null;
        if (string.IsNullOrWhiteSpace(forEach))
            return false;

        var match = ForEachSourcePattern().Match(forEach);
        if (!match.Success)
            return false;

        stepId = match.Groups[1].Value;
        path = match.Groups[2].Success ? match.Groups[2].Value : null;
        return true;
    }

    /// <summary>
    /// Resolves a <c>for_each</c> source to the JSON array it names (#779). On failure,
    /// <paramref name="error"/> says what was wrong in terms an author can act on.
    /// </summary>
    internal static bool TryResolveForEachSource(
        string? forEach,
        IReadOnlyDictionary<string, WispStepResult> priorResults,
        out JsonElement array,
        out string error)
    {
        array = default;
        error = "";

        if (!TryParseForEachSource(forEach, out var stepId, out var path))
        {
            error = $"'for_each' must be a single {{{{steps.<id>.result}}}} or {{{{steps.<id>.result.<path>}}}} template; got '{forEach}'.";
            return false;
        }

        var reference = path is null ? $"steps.{stepId}.result" : $"steps.{stepId}.result.{path}";

        if (!priorResults.TryGetValue(stepId, out var source))
        {
            error = $"'for_each' references step '{stepId}', which has not run before this step.";
            return false;
        }

        if (source.WasSkipped)
        {
            error = $"'for_each' references step '{stepId}', which was skipped (on_failure skip_to), so there is no list to iterate.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(source.Content))
        {
            error = $"'for_each' source {{{{{reference}}}}} is empty; step '{stepId}' produced no output to iterate.";
            return false;
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(source.Content);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            error = $"'for_each' source {{{{{reference}}}}} is not JSON: step '{stepId}' returned plain text. " +
                    "for_each needs a step whose output is JSON containing an array.";
            return false;
        }

        if (!TryNavigate(root, path, out var target))
        {
            error = $"'for_each' path '{path}' was not found in step '{stepId}''s output. " +
                    $"Its top-level fields are: {DescribeFields(root)}.";
            return false;
        }

        if (target.ValueKind != JsonValueKind.Array)
        {
            var hint = target.ValueKind == JsonValueKind.Object
                ? $" Point for_each at the array field inside it; its fields are: {DescribeFields(target)}."
                : "";
            error = $"'for_each' source {{{{{reference}}}}} is a JSON {target.ValueKind.ToString().ToLowerInvariant()}, not an array.{hint}";
            return false;
        }

        array = target;
        return true;
    }

    private static string DescribeFields(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? string.Join(", ", element.EnumerateObject().Select(p => $"{p.Name} ({p.Value.ValueKind.ToString().ToLowerInvariant()})").Take(20)) is { Length: > 0 } fields ? fields : "(none)"
            : $"(none — the output is a JSON {element.ValueKind.ToString().ToLowerInvariant()})";

    /// <summary>
    /// Parses <paramref name="json"/> and walks the dotted <paramref name="path"/>
    /// (object-property access only). Returns the extracted value as a string —
    /// JSON strings are unwrapped; other kinds serialize to their raw JSON text.
    /// Returns <c>false</c> when the content isn't a JSON object or the path misses.
    /// </summary>
    private static bool TryNavigateJsonPath(string json, string path, out string value)
    {
        value = "";
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryNavigate(doc.RootElement, path, out var current))
                return false;

            value = ElementText(current);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walks a dotted object-property <paramref name="path"/> from <paramref name="root"/>.
    /// A null or empty path returns the root itself.
    /// </summary>
    private static bool TryNavigate(JsonElement root, string? path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrEmpty(path))
            return true;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out var next))
                return false;
            value = next;
        }
        return true;
    }

    // JSON strings are unwrapped; null is empty; other kinds are their raw JSON text.
    private static string ElementText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => element.GetRawText()
    };

    [GeneratedRegex(@"\{\{(?:steps\.(?<step>[a-zA-Z0-9_\-]+)\.result(?:\.(?<spath>[a-zA-Z0-9_\-.]+))?|(?<name>[a-zA-Z_][a-zA-Z0-9_]*)(?:\.(?<ipath>[a-zA-Z0-9_\-.]+))?)\}\}")]
    private static partial Regex TemplatePattern();

    [GeneratedRegex(@"^\s*\{\{steps\.([a-zA-Z0-9_\-]+)\.result(?:\.([a-zA-Z0-9_\-.]+))?\}\}\s*$")]
    private static partial Regex ForEachSourcePattern();

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

/// <summary>
/// One element of a <c>for_each</c> step (#779): the element, its zero-based position, and the
/// step's optional <c>as</c> alias for it.
/// </summary>
internal sealed record ForEachItem(JsonElement Value, int Index, string? Alias = null)
{
    /// <summary>Whether <paramref name="name"/> refers to this element.</summary>
    public bool Matches(string name) =>
        string.Equals(name, GatewayRouter.ItemName, StringComparison.Ordinal)
        || (!string.IsNullOrEmpty(Alias) && string.Equals(name, Alias, StringComparison.Ordinal));
}
