using System.Text.Json;
using System.Text.RegularExpressions;
using RockBot.Tools.Mcp.Recovery;

namespace RockBot.Tools.Mcp;

/// <summary>
/// Hints the gateway appends to a failed MCP call so the model can correct itself on the next
/// attempt. The rule throughout is <b>positive evidence only</b>: a hint claims an argument
/// problem only when the arguments demonstrably contradict the tool's input schema, or when the
/// downstream's own words narrowly say it rejected a value. Anything else comes back unchanged —
/// a hint that blames the arguments for a server-side failure sends the model (and the human)
/// hunting through the payload for a bug that doesn't exist (mcp-aggregator#50).
/// <para>
/// Ported from mcp-aggregator's <c>ToolProxyHandler</c> (aggregator PRs #29, #36, #42, #51).
/// </para>
/// </summary>
public static class McpCallDiagnostics
{
    /// <summary>
    /// Phrases other MCP SDKs use when they reject an argument value: the TypeScript SDK,
    /// JSON-RPC's invalid-params code, and pydantic (Python SDK). Narrow on purpose — bare words
    /// like "validation" or "invalid argument" also appear in ordinary runtime errors.
    /// </summary>
    private static readonly string[] ValidationSignals =
        ["Invalid arguments for tool", "-32602", "validation error for"];

    /// <summary>
    /// The C# SDK's message for a tool that threw, with no detail after it. It means either a
    /// binding failure or a plain exception inside the tool, so it can only earn a hedged note.
    /// </summary>
    private static readonly Regex BareSdkFailure = new(
        @"^An error occurred invoking '[^']*'\.?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns a hint to append to a failed call's error text, or null when there is no positive
    /// evidence that the arguments caused the failure. Checks, in order: missing required or
    /// unrecognised keys; a top-level value whose JSON type contradicts the declared type; a
    /// narrow validation phrase from another SDK; the C# SDK's detail-free failure message.
    /// </summary>
    /// <param name="inputSchema">The tool's input schema as raw JSON, or null when unknown.</param>
    /// <param name="arguments">The arguments the model sent, before any gateway rewriting.</param>
    /// <param name="errorText">The downstream's error text.</param>
    public static string? DescribeArgumentProblem(
        string serverName,
        string toolName,
        string? inputSchema,
        IReadOnlyDictionary<string, object?> arguments,
        string? errorText)
    {
        using var schemaDoc = TryParseObject(inputSchema);
        var schema = schemaDoc?.RootElement;
        var schemaText = schema is { } s ? s.GetRawText() : null;

        if (schema is { } root)
        {
            var sent = JsonSerializer.SerializeToElement(arguments);
            var (missing, unknown) = CompareKeys(root, sent);

            if (missing.Count > 0 || unknown.Count > 0)
            {
                // Recovery already handles a downstream that names the missing field in words it
                // understands (environment defaults, then an enriched error). Repeating the schema
                // here would only duplicate what it adds.
                if (unknown.Count == 0
                    && SchemaErrorPatterns.TryExtractMissingField(errorText, out var named)
                    && missing.Contains(named, StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }

                var parts = new List<string> { $"Argument mismatch for tool '{toolName}' on '{serverName}'." };
                if (missing.Count > 0)
                    parts.Add($"Missing required parameter(s): [{string.Join(", ", missing)}].");
                if (unknown.Count > 0)
                    parts.Add($"Unrecognized argument key(s): [{string.Join(", ", unknown)}].");
                parts.Add($"You sent: [{string.Join(", ", arguments.Keys)}].");
                parts.Add($"Re-invoke with arguments matching this input schema: {schemaText}");
                return string.Join(" ", parts);
            }

            var mismatches = FindTypeMismatches(root, sent);
            if (mismatches.Count > 0)
            {
                return string.Join(" ", mismatches)
                       + $" Re-invoke '{toolName}' with values of the declared types. Input schema: {schemaText}";
            }
        }

        if (!string.IsNullOrEmpty(errorText)
            && ValidationSignals.Any(sig => errorText.Contains(sig, StringComparison.OrdinalIgnoreCase)))
        {
            return schemaText is null
                ? null
                : $"The downstream rejected a value in the arguments for '{toolName}'. Input schema: {schemaText}";
        }

        if (!string.IsNullOrEmpty(errorText) && BareSdkFailure.IsMatch(errorText.Trim()))
        {
            return $"'{toolName}' on '{serverName}' failed and the downstream gave no detail. " +
                   "Every argument key matched the schema, so if this is an argument problem it is in a " +
                   "nested value or a value's format; otherwise the failure is on the downstream's side." +
                   (schemaText is null ? "" : $" Input schema: {schemaText}");
        }

        return null;
    }

    /// <summary>
    /// Message for a tool name the server doesn't expose. Lists the real names so the model can
    /// pick one, and catches the common slip of passing a combined <c>server__tool</c> name.
    /// </summary>
    public static string DescribeUnknownTool(
        string serverName, string toolName, IReadOnlyCollection<string> availableTools)
    {
        var separator = toolName.IndexOf("__", StringComparison.Ordinal);
        if (separator > 0)
        {
            var prefix = toolName[..separator];
            var suffix = toolName[(separator + 2)..];
            var match = availableTools.FirstOrDefault(t => string.Equals(t, suffix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return $"'{toolName}' combines a server name and a tool name. " +
                       $"Pass server_name: \"{prefix}\" and tool_name: \"{match}\".";
            }
        }

        if (availableTools.Count == 0)
            return $"Unknown tool '{toolName}': server '{serverName}' exposes no tools.";

        return $"Unknown tool '{toolName}' on server '{serverName}'. " +
               $"Available tools: [{string.Join(", ", availableTools)}]. " +
               $"Re-invoke with one of those names, or call mcp_get_service_details(server_name: \"{serverName}\") for their schemas.";
    }

    /// <summary>Message for a server name that isn't registered at all.</summary>
    public static string DescribeUnknownServer(string serverName, IReadOnlyCollection<string> registeredServers)
    {
        if (registeredServers.Count == 0)
            return $"Unknown MCP server '{serverName}': no MCP servers are registered.";

        return $"Unknown MCP server '{serverName}'. " +
               $"Registered servers: [{string.Join(", ", registeredServers.Order(StringComparer.OrdinalIgnoreCase))}]. " +
               "It may have been renamed or removed — use one of those names, or call mcp_list_services to see what each provides.";
    }

    /// <summary>Message for a server that is registered but whose connection is down.</summary>
    public static string DescribeUnavailableServer(string serverName) =>
        $"MCP server '{serverName}' is registered but not reachable right now (its connection could not be established). " +
        "Try again later, or use another approach.";

    /// <summary>Message for a prompt name the server doesn't expose.</summary>
    public static string DescribeUnknownPrompt(
        string serverName, string promptName, IReadOnlyCollection<string> availablePrompts)
    {
        if (availablePrompts.Count == 0)
            return $"Unknown prompt '{promptName}': server '{serverName}' exposes no prompts.";

        return $"Unknown prompt '{promptName}' on server '{serverName}'. " +
               $"Available prompts: [{string.Join(", ", availablePrompts)}].";
    }

    /// <summary>
    /// Returns an error naming the required prompt arguments the call left out, or null when all
    /// are present. Lists every declared argument so one retry can get the call right.
    /// </summary>
    public static string? DescribeMissingPromptArguments(
        string serverName,
        string promptName,
        IReadOnlyList<McpPromptArgument> declared,
        IReadOnlyCollection<string> supplied)
    {
        var missing = declared
            .Where(a => a.Required && !supplied.Contains(a.Name, StringComparer.Ordinal))
            .Select(a => a.Name)
            .ToList();
        if (missing.Count == 0) return null;

        var signature = string.Join("; ", declared.Select(a =>
            $"{a.Name} ({(a.Required ? "required" : "optional")})" +
            (string.IsNullOrWhiteSpace(a.Description) ? "" : $": {a.Description}")));

        return $"Prompt '{promptName}' on '{serverName}' is missing required argument(s): [{string.Join(", ", missing)}]. " +
               $"Arguments: {signature}";
    }

    private static JsonDocument? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// Required keys absent from <paramref name="sent"/>, and sent keys the schema doesn't declare.
    /// A key sent as null counts as present: whether null is acceptable is the downstream's call.
    /// Unrecognised keys are only reported when the schema declares its properties.
    /// </summary>
    private static (List<string> Missing, List<string> Unknown) CompareKeys(JsonElement schema, JsonElement sent)
    {
        var missing = new List<string>();
        var unknown = new List<string>();
        var sentKeys = sent.ValueKind == JsonValueKind.Object
            ? sent.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in required.EnumerateArray())
            {
                if (r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } name && !sentKeys.Contains(name))
                    missing.Add(name);
            }
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in sentKeys)
            {
                if (!properties.TryGetProperty(key, out _))
                    unknown.Add(key);
            }
        }

        return (missing, unknown);
    }

    /// <summary>
    /// Top-level values whose JSON type contradicts the property's declared <c>type</c>. Skips
    /// nulls, undeclared keys, properties without a <c>type</c> (<c>anyOf</c>, <c>$ref</c>, …)
    /// and anything nested, so every mismatch reported is real.
    /// </summary>
    private static List<string> FindTypeMismatches(JsonElement schema, JsonElement sent)
    {
        var result = new List<string>();
        if (sent.ValueKind != JsonValueKind.Object) return result;
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var arg in sent.EnumerateObject())
        {
            if (arg.Value.ValueKind == JsonValueKind.Null) continue;
            if (!properties.TryGetProperty(arg.Name, out var property) || property.ValueKind != JsonValueKind.Object)
                continue;
            if (!property.TryGetProperty("type", out var typeElement)) continue;

            var declared = DeclaredTypes(typeElement);
            if (declared.Count == 0 || declared.Any(t => !KnownTypes.Contains(t))) continue;
            if (declared.Any(t => Satisfies(arg.Value, t))) continue;

            result.Add($"Parameter '{arg.Name}' is declared as {string.Join(" or ", declared)} but you sent {Describe(arg.Value)}.");
        }

        return result;
    }

    private static readonly HashSet<string> KnownTypes =
        new(StringComparer.Ordinal) { "string", "number", "integer", "boolean", "array", "object", "null" };

    private static List<string> DeclaredTypes(JsonElement typeElement) => typeElement.ValueKind switch
    {
        JsonValueKind.String when typeElement.GetString() is { Length: > 0 } t => [t],
        JsonValueKind.Array => typeElement.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList(),
        _ => []
    };

    private static bool Satisfies(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && IsWholeNumber(value),
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static bool IsWholeNumber(JsonElement value) =>
        value.TryGetInt64(out _) || (value.TryGetDouble(out var d) && double.IsFinite(d) && Math.Floor(d) == d);

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "a string",
        JsonValueKind.Number => IsWholeNumber(value) ? "an integer" : "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => "a value"
    };
}
