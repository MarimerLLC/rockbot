using System.Text;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Renders an elicitation's requested schema as plain text — for the responder's prompt and for
/// the note the agent sees when the bridge could not answer.
/// </summary>
/// <remarks>
/// MCP restricts an elicitation schema to a flat object of primitives, so a short line per field
/// says everything a reader (or a model) needs. No JSON Schema dialect is emitted on purpose:
/// the responder is asked for values, not for a schema it might echo back.
/// </remarks>
public static class McpElicitationSchemaDescriber
{
    /// <summary>Field names in the order the server declared them.</summary>
    public static IReadOnlyList<string> FieldNames(ElicitRequestParams.RequestSchema? schema)
        => schema?.Properties is { Count: > 0 } properties
            ? properties.Keys.ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// One line per field: name, type, whether it is required, its description, and whatever
    /// constraints the server attached. Returns an empty string when there are no fields.
    /// </summary>
    public static string Describe(ElicitRequestParams.RequestSchema? schema)
    {
        if (schema?.Properties is not { Count: > 0 } properties)
            return string.Empty;

        var required = schema.Required is { Count: > 0 }
            ? new HashSet<string>(schema.Required, StringComparer.Ordinal)
            : null;

        var builder = new StringBuilder();
        foreach (var pair in properties)
        {
            // Field names and option values are server-authored too, so they are flattened like
            // the description — otherwise a name could forge its own lines in the field list.
            builder.Append("- ").Append(Flatten(pair.Key));
            builder.Append(" (").Append(Flatten(DescribeType(pair.Value)));
            if (required?.Contains(pair.Key) == true)
                builder.Append(", required");
            builder.Append(')');

            var text = pair.Value?.Description ?? pair.Value?.Title;
            if (!string.IsNullOrWhiteSpace(text))
                builder.Append(": ").Append(Flatten(text));

            var constraints = DescribeConstraints(pair.Value);
            if (constraints.Length > 0)
                builder.Append(' ').Append(constraints);

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    private static string DescribeType(ElicitRequestParams.PrimitiveSchemaDefinition? definition) => definition switch
    {
        null => "unknown type",
        ElicitRequestParams.StringSchema s => s.Format is { Length: > 0 } f ? $"string, {f}" : "string",
        ElicitRequestParams.NumberSchema n => n.Type,
        ElicitRequestParams.BooleanSchema => "boolean",
        ElicitRequestParams.UntitledSingleSelectEnumSchema e => $"one of: {string.Join(", ", e.Enum)}",
        ElicitRequestParams.TitledSingleSelectEnumSchema e => $"one of: {string.Join(", ", e.OneOf.Select(o => o.Const))}",
        ElicitRequestParams.UntitledMultiSelectEnumSchema e => $"any of: {string.Join(", ", e.Items.Enum)}",
        ElicitRequestParams.TitledMultiSelectEnumSchema e => $"any of: {string.Join(", ", e.Items.AnyOf.Select(o => o.Const))}",
        _ => definition.Type,
    };

    private static string DescribeConstraints(ElicitRequestParams.PrimitiveSchemaDefinition? definition)
    {
        var parts = new List<string>(2);
        switch (definition)
        {
            case ElicitRequestParams.StringSchema s:
                if (s.MinLength is { } min) parts.Add($"min length {min}");
                if (s.MaxLength is { } max) parts.Add($"max length {max}");
                break;
            case ElicitRequestParams.NumberSchema n:
                if (n.Minimum is { } lo) parts.Add($"min {lo}");
                if (n.Maximum is { } hi) parts.Add($"max {hi}");
                break;
            case ElicitRequestParams.UntitledMultiSelectEnumSchema m:
                if (m.MinItems is { } mi) parts.Add($"at least {mi}");
                if (m.MaxItems is { } ma) parts.Add($"at most {ma}");
                break;
            case ElicitRequestParams.TitledMultiSelectEnumSchema m:
                if (m.MinItems is { } tmi) parts.Add($"at least {tmi}");
                if (m.MaxItems is { } tma) parts.Add($"at most {tma}");
                break;
        }

        return parts.Count == 0 ? string.Empty : $"[{string.Join("; ", parts)}]";
    }

    /// <summary>
    /// Collapses server-supplied text onto one line. Server text is untrusted and multi-line
    /// text would let it forge its own bullet lines inside the rendered field list.
    /// </summary>
    internal static string Flatten(string text)
        => text.Replace("\r", " ").Replace("\n", " ").Trim();
}
