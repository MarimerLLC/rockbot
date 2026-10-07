using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.TodoApp;

/// <summary>
/// Makes every tool's argument contract strict. The SDK binds arguments to method
/// parameters and silently drops keys that match none, so a misspelled or unsupported
/// argument turns into a successful no-op. These filters reject such calls and
/// advertise <c>additionalProperties: false</c> so clients can validate up front.
/// </summary>
public static class StrictArguments
{
    public static IMcpServerBuilder WithStrictArguments(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters =>
        {
            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                if (context.MatchedPrimitive is McpServerTool tool && context.Params?.Arguments is { Count: > 0 } arguments
                    && Validate(tool.ProtocolTool, arguments) is { } error)
                {
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error }] };
                }

                return await next(context, cancellationToken);
            });

            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                foreach (var tool in result.Tools)
                    tool.InputSchema = DisallowAdditionalProperties(tool.InputSchema);
                return result;
            });
        });

    /// <summary>Returns an error message when the arguments don't fit the tool's input schema, otherwise null.</summary>
    public static string? Validate(Tool tool, IDictionary<string, JsonElement> arguments)
    {
        var unknown = FindUnknownArguments(tool.InputSchema, arguments.Keys);
        if (unknown.Count > 0)
        {
            return $"unknown argument(s) {string.Join(", ", unknown.Select(k => $"'{k}'"))} " +
                   $"for {tool.Name}; valid arguments: {string.Join(", ", GetPropertyNames(tool.InputSchema))}";
        }

        // The SDK reports an unbindable enum value only as a generic invocation error, so check it here.
        foreach (var (name, value) in arguments)
        {
            if (FindAllowedValues(tool.InputSchema, name) is { } allowed
                && value.ValueKind == JsonValueKind.String
                && !allowed.Contains(value.GetString()!, StringComparer.OrdinalIgnoreCase))
            {
                return $"invalid value '{value.GetString()}' for {name}; expected one of: {string.Join(", ", allowed)}";
            }
        }

        return null;
    }

    /// <summary>The string values a property's schema allows via <c>enum</c>, or null when it isn't an enum.</summary>
    public static IReadOnlyList<string>? FindAllowedValues(JsonElement inputSchema, string propertyName)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var properties)
            || !properties.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object
            || !property.TryGetProperty("enum", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return values.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString()!)
            .ToList();
    }

    /// <summary>Returns the argument names that are not declared in the tool's input schema.</summary>
    public static IReadOnlyList<string> FindUnknownArguments(JsonElement inputSchema, IEnumerable<string> argumentNames)
    {
        var declared = GetPropertyNames(inputSchema).ToHashSet(StringComparer.Ordinal);
        return argumentNames.Where(name => !declared.Contains(name)).ToList();
    }

    /// <summary>Sets <c>additionalProperties: false</c> on the schema root unless it is already specified.</summary>
    public static JsonElement DisallowAdditionalProperties(JsonElement inputSchema)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object || inputSchema.TryGetProperty("additionalProperties", out _))
            return inputSchema;

        var node = JsonNode.Parse(inputSchema.GetRawText())!.AsObject();
        node["additionalProperties"] = false;
        return JsonSerializer.SerializeToElement(node);
    }

    private static IEnumerable<string> GetPropertyNames(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object
        && inputSchema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name)
            : [];
}
