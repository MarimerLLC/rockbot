using System.Text.Json;

namespace RockBot.Tools.Mcp;

/// <summary>
/// <c>mcp_find_tools(query, limit)</c>, lazy mode's way in (#612, porting mcp-aggregator#42's
/// <c>find_tools</c>). Ranks the typed tools with <see cref="McpToolSearch"/>, activates the
/// matches for the calling session, and returns each with its full input schema. Activated
/// tools are callable by typed name from the agent loop's next iteration.
/// <para>
/// There is no "return schemas without activating" variant: a schema the model can't call by
/// name is just <c>mcp_invoke_tool</c> with extra steps, which the aggregator found too.
/// </para>
/// </summary>
public sealed class McpFindToolsExecutor(McpTypedToolSurface surface, McpServerIndex index) : IToolExecutor
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 25;

    public const string Description =
        "Search the connected MCP servers' tools by keyword (e.g. \"send email\", \"list calendar events\"). " +
        "Each match comes back with its typed name ({server}__{tool}) and full parameter schema, and becomes " +
        "callable by that name in this conversation — call it next, as a normal tool. Search again with other " +
        "words if nothing fits.";

    public const string ParametersSchema =
        """{"type":"object","properties":{"query":{"type":"string","description":"Keywords for the action or data you need, e.g. 'send email' or 'calendar events'"},"limit":{"type":"integer","description":"Most results to return (default 10, max 25)"}},"required":["query"]}""";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, object?> args;
        try
        {
            args = McpToolExecutor.ParseArguments(request.Arguments);
        }
        catch (JsonException ex)
        {
            return Task.FromResult(Error(request, $"Arguments for {McpTypedToolSurface.FindToolsName} must be a JSON object: {ex.Message}"));
        }

        if (!args.TryGetValue("query", out var queryValue) || ToText(queryValue) is not { Length: > 0 } query)
            return Task.FromResult(Error(request, "Missing required parameter: query"));

        var limit = args.TryGetValue("limit", out var limitValue) && ToInt(limitValue) is int requested
            ? Math.Clamp(requested, 1, MaxLimit)
            : DefaultLimit;

        var matches = surface.Find(query, index.Servers, limit);
        var active = surface.Activate(request.SessionId, matches.Select(m => m.Tool)).ToHashSet(StringComparer.Ordinal);

        var payload = new
        {
            query,
            tools = matches.Select(m => new
            {
                name = m.Tool.Name,
                server = m.Tool.ServerName,
                serverId = m.Tool.ServerId,
                tool = m.Tool.ToolName,
                description = m.Tool.Description,
                inputSchema = ParseSchema(m.Tool.InputSchema),
                activated = active.Contains(m.Tool.Name)
            }),
            note = Note(matches.Count, active.Count)
        };

        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = JsonSerializer.Serialize(payload, JsonOptions)
        });
    }

    private static string Note(int matches, int activated) => (matches, activated) switch
    {
        (0, _) => "No tools matched. Try different or fewer keywords, or mcp_list_services to see every server.",
        (_, 0) => "These tools could not be activated for this conversation. Call them through " +
                  "mcp_invoke_tool(server_name, tool_name, arguments).",
        _ => "Tools marked activated are now callable by their typed name. Call the one you need directly."
    };

    private static JsonElement? ParseSchema(string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(schema);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ToText(object? value) => value switch
    {
        string s => s.Trim(),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()?.Trim(),
        _ => null
    };

    private static int? ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)Math.Clamp(l, int.MinValue, int.MaxValue),
        double d => (int)d,
        string s when int.TryParse(s, out var parsed) => parsed,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt32(out var n) => n,
        JsonElement { ValueKind: JsonValueKind.String } e when int.TryParse(e.GetString(), out var parsed) => parsed,
        _ => null
    };

    private static ToolInvokeResponse Error(ToolInvokeRequest request, string message) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Content = message,
        IsError = true
    };
}
