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
                if (context.MatchedPrimitive is McpServerTool tool && context.Params?.Arguments is { Count: > 0 } arguments)
                {
                    var unknown = FindUnknownArguments(tool.ProtocolTool.InputSchema, arguments.Keys);
                    if (unknown.Count > 0)
                    {
                        var valid = GetPropertyNames(tool.ProtocolTool.InputSchema);
                        return new CallToolResult
                        {
                            IsError = true,
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = $"unknown argument(s) {string.Join(", ", unknown.Select(k => $"'{k}'"))} " +
                                           $"for {tool.ProtocolTool.Name}; valid arguments: {string.Join(", ", valid)}"
                                }
                            ]
                        };
                    }
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
