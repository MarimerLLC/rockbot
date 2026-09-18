using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RockBot.Tools.Mcp.Elicitation;

/// <summary>
/// Result of checking a proposed elicitation answer against the schema the server asked for.
/// </summary>
/// <param name="Content">The accepted field values, ready to send back as <c>content</c>.</param>
/// <param name="Errors">Why the answer was rejected. Empty means it is usable.</param>
/// <param name="IgnoredFields">Fields the answer invented that the server never asked for.</param>
public sealed record McpElicitationValidationResult(
    IReadOnlyDictionary<string, JsonElement> Content,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> IgnoredFields)
{
    /// <summary>Whether the answer satisfies the schema and can be sent to the server.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates a proposed elicitation answer against the server's requested schema.
/// </summary>
/// <remarks>
/// <para>
/// This is the trust boundary for elicitation. Whatever produced the answer — a model, a
/// configured default, eventually a person — the bridge only forwards values that fit the shape
/// the server declared. Nothing is coerced: a string where a number was asked for is an error,
/// not a parse, because a server that receives a plausible-looking wrong type will act on it.
/// </para>
/// <para>
/// Fields the answer invented are dropped rather than rejected, and fields the answer omits are
/// left out entirely so the SDK can apply the schema's own defaults afterwards.
/// </para>
/// </remarks>
public static class McpElicitationSchemaValidator
{
    /// <summary>
    /// Validates <paramref name="proposed"/> against <paramref name="schema"/>.
    /// </summary>
    public static McpElicitationValidationResult Validate(
        ElicitRequestParams.RequestSchema? schema,
        IReadOnlyDictionary<string, JsonElement>? proposed)
    {
        var content = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var errors = new List<string>();
        var ignored = new List<string>();

        var properties = schema?.Properties;
        if (properties is not { Count: > 0 })
        {
            // No schema means the server asked for confirmation only; anything proposed is noise.
            if (proposed is { Count: > 0 })
                ignored.AddRange(proposed.Keys);
            return new McpElicitationValidationResult(content, errors, ignored);
        }

        if (proposed is { Count: > 0 })
        {
            foreach (var pair in proposed)
            {
                if (!properties.TryGetValue(pair.Key, out var definition))
                {
                    ignored.Add(pair.Key);
                    continue;
                }

                var error = ValidateValue(pair.Key, definition, pair.Value);
                if (error is not null)
                    errors.Add(error);
                else
                    content[pair.Key] = pair.Value.Clone();
            }
        }

        if (schema!.Required is { Count: > 0 } required)
        {
            foreach (var name in required)
            {
                if (!content.ContainsKey(name))
                    errors.Add($"required field '{name}' was not answered");
            }
        }

        return new McpElicitationValidationResult(content, errors, ignored);
    }

    private static string? ValidateValue(
        string name,
        ElicitRequestParams.PrimitiveSchemaDefinition definition,
        JsonElement value)
    {
        switch (definition)
        {
            case ElicitRequestParams.StringSchema s:
                if (value.ValueKind != JsonValueKind.String)
                    return Wrong(name, "a string", value);
                var text = value.GetString() ?? string.Empty;
                if (s.MinLength is { } min && text.Length < min)
                    return $"field '{name}' is shorter than the required {min} characters";
                if (s.MaxLength is { } max && text.Length > max)
                    return $"field '{name}' is longer than the allowed {max} characters";
                return null;

            case ElicitRequestParams.NumberSchema n:
                if (value.ValueKind != JsonValueKind.Number)
                    return Wrong(name, $"a {n.Type}", value);
                if (!value.TryGetDouble(out var number))
                    return $"field '{name}' is not a number the bridge can read";
                if (string.Equals(n.Type, "integer", StringComparison.Ordinal) && !value.TryGetInt64(out _))
                    return $"field '{name}' must be a whole number";
                if (n.Minimum is { } lo && number < lo)
                    return $"field '{name}' is below the minimum of {lo}";
                if (n.Maximum is { } hi && number > hi)
                    return $"field '{name}' is above the maximum of {hi}";
                return null;

            case ElicitRequestParams.BooleanSchema:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? null
                    : Wrong(name, "true or false", value);

            case ElicitRequestParams.UntitledSingleSelectEnumSchema e:
                return ValidateChoice(name, value, e.Enum);

            case ElicitRequestParams.TitledSingleSelectEnumSchema e:
                return ValidateChoice(name, value, [.. e.OneOf.Select(o => o.Const)]);

            case ElicitRequestParams.UntitledMultiSelectEnumSchema e:
                return ValidateChoices(name, value, e.Items.Enum, e.MinItems, e.MaxItems);

            case ElicitRequestParams.TitledMultiSelectEnumSchema e:
                return ValidateChoices(name, value, [.. e.Items.AnyOf.Select(o => o.Const)], e.MinItems, e.MaxItems);

#pragma warning disable MCP9001 // deprecated by the spec, still emitted by older servers
            case ElicitRequestParams.LegacyTitledEnumSchema e:
                return ValidateChoice(name, value, e.Enum);
#pragma warning restore MCP9001

            default:
                // A schema shape this SDK version does not model. Pass the value through rather
                // than blocking the call — the server validates its own schema either way.
                return null;
        }
    }

    private static string? ValidateChoice(string name, JsonElement value, IList<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.String)
            return Wrong(name, "one of the offered choices", value);

        var text = value.GetString();
        return allowed.Contains(text ?? string.Empty, StringComparer.Ordinal)
            ? null
            : $"field '{name}' must be one of: {string.Join(", ", allowed)}";
    }

    private static string? ValidateChoices(
        string name,
        JsonElement value,
        IList<string> allowed,
        int? minItems,
        int? maxItems)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return Wrong(name, "an array of the offered choices", value);

        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            count++;
            if (item.ValueKind != JsonValueKind.String
                || !allowed.Contains(item.GetString() ?? string.Empty, StringComparer.Ordinal))
            {
                return $"field '{name}' may only contain: {string.Join(", ", allowed)}";
            }
        }

        if (minItems is { } min && count < min)
            return $"field '{name}' needs at least {min} value(s)";
        if (maxItems is { } max && count > max)
            return $"field '{name}' allows at most {max} value(s)";

        return null;
    }

    private static string Wrong(string name, string expected, JsonElement value)
        => $"field '{name}' must be {expected}, not {value.ValueKind.ToString().ToLowerInvariant()}";
}
