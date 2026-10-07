using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using RockBot.McpMeasure.Fixtures;
using RockBot.Tools.Mcp;

namespace RockBot.McpMeasure.Measurement;

/// <summary>The failure taxonomy (#613), plus <see cref="Ok"/> for a call a server accepts.</summary>
public static class Outcome
{
    public const string Ok = "ok";

    // A call that doesn't reach a tool, or reaches it with arguments its schema rejects.
    public const string EmptyArguments = "empty_arguments";
    public const string InventedServer = "invented_server";
    public const string TypedNameAsToolName = "typed_name_as_tool_name";
    public const string InventedTool = "invented_tool";
    public const string BareToolName = "bare_tool_name";
    public const string WrongKey = "wrong_key";
    public const string WrongType = "wrong_type";

    // A well-formed call that isn't the one the user asked for.
    public const string WrongServer = "wrong_server";
    public const string OtherTool = "other_tool";
    public const string WrongValue = "wrong_value";

    // No downstream call at all.
    public const string ToolCallAsText = "tool_call_as_text";
    public const string NoAttempt = "no_attempt";
    public const string Error = "error";
}

/// <summary>One call the model made that tried to reach a downstream tool.</summary>
public sealed record DownstreamAttempt(
    string Via,          // typed | invoke_tool | bare
    string CalledName,
    string? Server,
    string? Tool,
    string ArgsJson,
    string Outcome,
    string? Detail);

/// <summary>What one turn's calls amount to.</summary>
public sealed record TurnGrade(
    IReadOnlyList<DownstreamAttempt> Attempts,
    int MetaCalls,
    bool FirstCallWellFormed,
    bool FirstCallCorrect,
    string? FirstCallFailure);

/// <summary>
/// Turns the calls the model made into downstream attempts and classifies each. Mirrors what the
/// gateway tolerates — <c>mcp_invoke_tool</c> takes its nested arguments under <c>arguments</c>,
/// <c>params</c> or <c>args</c>, or flattened at the top level, and lowercases the server name —
/// so an attempt counts as well-formed exactly when the gateway would deliver it.
/// </summary>
public sealed class Grader
{
    private static readonly string[] NestedArgAliases = ["arguments", "params", "args"];
    private static readonly HashSet<string> InvokeKeys = new(StringComparer.OrdinalIgnoreCase)
        { "server_name", "tool_name", "arguments", "params", "args" };

    // Text that looks like a tool call the model wrote out instead of making.
    private static readonly Regex TextCall = new(
        @"mcp_invoke_tool|""?server_name""?\s*[:=]|tool_call_name:|to=functions\.|\b[a-z0-9-]+__[a-z0-9_]+\s*\(|<tool_call>|""name""\s*:\s*""[a-z0-9_-]+(__[a-z0-9_]+)?""\s*,\s*""arguments""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, Dictionary<string, JsonElement>> _schemas;
    private readonly McpWrapperCatalog _catalog;

    public Grader(IEnumerable<FixtureServer> servers, McpWrapperCatalog catalog)
    {
        _catalog = catalog;
        _schemas = servers.ToDictionary(
            s => s.Name,
            s => s.Tools.ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.InputSchema, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
    }

    public TurnGrade Grade(MeasureTurn turn, IEnumerable<FunctionCallContent> calls, string? finalText)
    {
        var attempts = new List<DownstreamAttempt>();
        var meta = 0;
        foreach (var call in calls)
        {
            if (ToAttempt(call) is { } attempt)
                attempts.Add(attempt);
            else
                meta++;
        }

        if (attempts.Count == 0)
        {
            var failure = finalText is not null && TextCall.IsMatch(finalText) ? Outcome.ToolCallAsText : Outcome.NoAttempt;
            return new TurnGrade(attempts, meta, false, false, failure);
        }

        var first = attempts[0];
        var wellFormed = first.Outcome == Outcome.Ok;
        string? firstFailure = first.Outcome;
        var correct = false;
        if (wellFormed)
        {
            if (!string.Equals(first.Tool, turn.Tool, StringComparison.Ordinal))
                firstFailure = Outcome.OtherTool;
            else if (!string.Equals(first.Server, turn.Server, StringComparison.OrdinalIgnoreCase))
                firstFailure = Outcome.WrongServer;
            else if (turn.Check(JsonDocument.Parse(first.ArgsJson).RootElement) is not null)
                firstFailure = Outcome.WrongValue;
            else
            {
                correct = true;
                firstFailure = null;
            }
        }

        return new TurnGrade(attempts, meta, wellFormed, correct, firstFailure);
    }

    /// <summary>The call as a downstream attempt, or null when it is a gateway or other tool.</summary>
    public DownstreamAttempt? ToAttempt(FunctionCallContent call)
    {
        var args = ToJson(call.Arguments);

        if (call.Name == "mcp_invoke_tool")
            return InvokeAttempt(call.Name, args);

        if (_catalog.TryGet(call.Name, out var wrapper))
        {
            var (outcome, detail) = CheckSchema(wrapper.ServerName, wrapper.ToolName, args);
            return new("typed", call.Name, wrapper.ServerName, wrapper.ToolName, args.GetRawText(), outcome, detail);
        }

        var separator = call.Name.IndexOf("__", StringComparison.Ordinal);
        if (separator > 0)
        {
            var server = call.Name[..separator];
            var tool = call.Name[(separator + 2)..];
            return _schemas.ContainsKey(server)
                ? new("typed", call.Name, server, tool, args.GetRawText(), Outcome.InventedTool, "no such typed tool")
                : new("typed", call.Name, server, tool, args.GetRawText(), Outcome.InventedServer, "no such server");
        }

        // A downstream tool's own name, called as if it were in the tool list.
        var owner = _schemas.FirstOrDefault(s => s.Value.ContainsKey(call.Name)).Key;
        return owner is null
            ? null
            : new("bare", call.Name, owner, call.Name, args.GetRawText(), Outcome.BareToolName, "not a tool in the list");
    }

    private DownstreamAttempt InvokeAttempt(string name, JsonElement args)
    {
        string raw = args.GetRawText();
        var server = String(args, "server_name")?.ToLowerInvariant();
        var tool = String(args, "tool_name");

        if (args.ValueKind != JsonValueKind.Object || !args.EnumerateObject().Any())
            return new("invoke_tool", name, null, null, raw, Outcome.EmptyArguments, "no arguments at all");
        if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(tool))
            return new("invoke_tool", name, server, tool, raw, Outcome.EmptyArguments, "server_name or tool_name missing");
        if (tool.Contains("__", StringComparison.Ordinal))
            return new("invoke_tool", name, server, tool, raw, Outcome.TypedNameAsToolName, null);
        if (!_schemas.TryGetValue(server, out var tools))
            return new("invoke_tool", name, server, tool, raw, Outcome.InventedServer, null);
        if (!tools.ContainsKey(tool))
        {
            return _schemas.Any(s => s.Value.ContainsKey(tool))
                ? new("invoke_tool", name, server, tool, raw, Outcome.WrongServer, "tool is on another server")
                : new("invoke_tool", name, server, tool, raw, Outcome.InventedTool, null);
        }

        // The inner arguments: nested under one of the aliases, or flattened at the top level.
        JsonElement inner;
        var nested = NestedArgAliases
            .Select(alias => args.TryGetProperty(alias, out var v) && v.ValueKind != JsonValueKind.Null ? v : (JsonElement?)null)
            .FirstOrDefault(v => v is not null);
        if (nested is { } n)
        {
            if (n.ValueKind != JsonValueKind.Object)
                return new("invoke_tool", name, server, tool, raw, Outcome.WrongType, $"'arguments' is a {n.ValueKind}, not an object");
            inner = n;
        }
        else
        {
            var flat = args.EnumerateObject().Where(p => !InvokeKeys.Contains(p.Name))
                .ToDictionary(p => p.Name, p => p.Value);
            inner = JsonSerializer.SerializeToElement(flat);
        }

        var (outcome, detail) = CheckSchema(server, tool, inner);
        return new("invoke_tool", name, server, tool, inner.GetRawText(), outcome, detail);
    }

    /// <summary>Checks <paramref name="args"/> against the tool's input schema: required keys and JSON types.</summary>
    public (string Outcome, string? Detail) CheckSchema(string server, string tool, JsonElement args)
    {
        if (!_schemas.TryGetValue(server, out var tools) || !tools.TryGetValue(tool, out var schema))
            return (Outcome.InventedTool, null);

        var present = args.ValueKind == JsonValueKind.Object
            ? args.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToDictionary(p => p.Name, p => p.Value)
            : [];

        var required = schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(e => e.GetString()!).ToList()
            : [];
        var missing = required.Where(k => !present.ContainsKey(k)).ToList();
        if (missing.Count > 0)
        {
            return present.Count == 0
                ? (Outcome.EmptyArguments, $"missing {string.Join(", ", missing)}")
                : (Outcome.WrongKey, $"missing {string.Join(", ", missing)}; sent {string.Join(", ", present.Keys)}");
        }

        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var (key, value) in present)
            {
                if (properties.TryGetProperty(key, out var property) && TypeProblem(property, value) is { } problem)
                    return (Outcome.WrongType, $"'{key}': {problem}");
            }
        }

        return (Outcome.Ok, null);
    }

    private static string? TypeProblem(JsonElement property, JsonElement value)
    {
        var types = property.TryGetProperty("type", out var t)
            ? t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(e => e.GetString()).ToList() : [t.GetString()]
            : [];
        if (types.Count == 0)
            return null;

        var actual = value.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Array => "array",
            JsonValueKind.Object => "object",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => value.TryGetInt64(out _) ? "integer" : "number",
            _ => "null"
        };

        var fits = types.Contains(actual) || (actual == "integer" && types.Contains("number"));
        if (!fits)
            return $"expected {string.Join("|", types)}, got {actual}";

        if (actual == "string" && property.TryGetProperty("format", out var format)
            && format.GetString() is "date-time" or "date"
            && !DateTime.TryParse(value.GetString(), out _))
            return $"'{value.GetString()}' is not a date";

        return null;
    }

    private static string? String(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static JsonElement ToJson(IDictionary<string, object?>? arguments) =>
        JsonSerializer.SerializeToElement(arguments ?? new Dictionary<string, object?>());
}
