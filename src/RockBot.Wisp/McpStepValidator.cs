using System.Text;
using System.Text.Json;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Validates MCP wisp step parameters against the target tool's JSON Schema
/// before the step is invoked. Catches the common authoring failure mode where
/// the LLM composes a wisp from training priors (e.g. <c>startDate</c>/<c>endDate</c>)
/// rather than the tool's real contract (e.g. <c>timeMin</c>/<c>timeMax</c>).
///
/// Validation is intentionally narrow — "required fields present" and (when the
/// schema says so) "unknown fields rejected". On failure, the error message
/// includes a compact schema summary so an auto-retry call can correct the
/// params without re-discovering the tool.
/// </summary>
internal static class McpStepValidator
{
    /// <summary>
    /// Structured validation outcome. <see cref="Error"/> is non-null exactly when
    /// either <see cref="MissingFields"/> or <see cref="UnknownFields"/> is non-empty.
    /// <see cref="MissingFields"/> is exposed so callers (e.g. wisp pre-flight recovery)
    /// can hand the field list to <see cref="IMcpPreflightRecovery"/> for environmental
    /// fills and schema-error enrichment.
    /// </summary>
    public sealed record Result(
        IReadOnlyList<string> MissingFields,
        IReadOnlyList<string> UnknownFields,
        WispStepError? Error);

    private static readonly Result EmptyResult = new([], [], null);

    /// <summary>
    /// Validates an MCP-gateway step's resolved params against the target tool's
    /// schema. Returns <c>null</c> if valid, a schema-free step (non-MCP, or the
    /// tool is unregistered/unschematized), or if the params JSON is malformed —
    /// the existing executor path will produce a more informative failure in those
    /// cases. Returns a <see cref="WispStepError"/> with <see cref="FailureCategory.Structural"/>
    /// when required fields are missing or unknown fields are present under a
    /// closed schema.
    /// </summary>
    public static WispStepError? Validate(WispStep step, IToolRegistry registry) =>
        ValidateDetailed(step, registry).Error;

    /// <summary>
    /// Same as <see cref="Validate"/> but exposes the missing and unknown field lists
    /// structurally so callers can route them through <see cref="IMcpPreflightRecovery"/>
    /// before falling back to LLM auto-correction.
    /// </summary>
    public static Result ValidateDetailed(WispStep step, IToolRegistry registry)
    {
        if (step.Gateway != GatewayType.Mcp)
            return EmptyResult;
        if (string.IsNullOrEmpty(step.Server) || string.IsNullOrEmpty(step.Tool))
            return EmptyResult;

        var tool = FindMcpTool(registry, step.Server, step.Tool);
        if (tool?.ParametersSchema is null)
            return EmptyResult;

        JsonElement schema;
        try
        {
            schema = JsonDocument.Parse(tool.ParametersSchema).RootElement;
        }
        catch (JsonException)
        {
            return EmptyResult;
        }

        var paramsElement = step.ResolvedParams;
        if (paramsElement is null || paramsElement.Value.ValueKind != JsonValueKind.Object)
            paramsElement = EmptyObject;

        var (missing, unknown) = Compare(schema, paramsElement.Value);
        if (missing.Count == 0 && unknown.Count == 0)
            return EmptyResult;

        var error = new WispStepError
        {
            Category = FailureCategory.Structural,
            Message = BuildErrorMessage(step.Server!, step.Tool!, missing, unknown, schema),
            ToolName = $"{step.Server}/{step.Tool}"
        };
        return new Result(missing, unknown, error);
    }

    private static ToolRegistration? FindMcpTool(IToolRegistry registry, string server, string tool)
    {
        var source = $"mcp:{server}";
        foreach (var reg in registry.GetTools())
        {
            if (string.Equals(reg.Name, tool, StringComparison.Ordinal)
                && string.Equals(reg.Source, source, StringComparison.Ordinal))
                return reg;
        }
        return null;
    }

    private static (List<string> Missing, List<string> Unknown) Compare(JsonElement schema, JsonElement paramsObj)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredEl)
            && requiredEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    required.Add(item.GetString()!);
        }

        var known = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("properties", out var propsEl)
            && propsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in propsEl.EnumerateObject())
                known.Add(p.Name);
        }

        var closed = schema.TryGetProperty("additionalProperties", out var addEl)
                     && addEl.ValueKind == JsonValueKind.False;

        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paramsObj.EnumerateObject())
            supplied.Add(p.Name);

        var missing = required.Where(r => !supplied.Contains(r)).ToList();
        var unknown = closed
            ? supplied.Where(s => !known.Contains(s)).ToList()
            : [];

        return (missing, unknown);
    }

    private static string BuildErrorMessage(
        string server, string tool,
        List<string> missing, List<string> unknown,
        JsonElement schema)
    {
        var sb = new StringBuilder();
        sb.Append($"Params for {server}/{tool} did not match the tool's schema.");
        if (missing.Count > 0)
            sb.Append(" Missing required field(s): ").Append(string.Join(", ", missing)).Append('.');
        if (unknown.Count > 0)
            sb.Append(" Unknown field(s): ").Append(string.Join(", ", unknown)).Append('.');

        sb.AppendLine();
        sb.Append("Expected shape:");
        sb.AppendLine();
        AppendSchemaSummary(sb, schema);

        return sb.ToString().TrimEnd();
    }

    private static void AppendSchemaSummary(StringBuilder sb, JsonElement schema)
    {
        if (!schema.TryGetProperty("properties", out var propsEl)
            || propsEl.ValueKind != JsonValueKind.Object)
        {
            sb.Append("  (schema has no 'properties' block)");
            return;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var requiredEl)
            && requiredEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requiredEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    required.Add(item.GetString()!);
        }

        foreach (var prop in propsEl.EnumerateObject())
        {
            var type = prop.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : "any";
            var req = required.Contains(prop.Name) ? " (required)" : "";
            var desc = prop.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? " — " + Truncate(d.GetString()!, 80)
                : "";
            sb.AppendLine($"  {prop.Name}: {type}{req}{desc}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonElement EmptyObject =
        JsonDocument.Parse("{}").RootElement;
}
