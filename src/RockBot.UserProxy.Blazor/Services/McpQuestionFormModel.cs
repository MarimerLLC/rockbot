using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RockBot.UserProxy.Blazor.Services;

/// <summary>What the user sent from a question's form: the answer JSON, or a decline.</summary>
public sealed record McpQuestionSubmission(string QuestionId, string? AnswersJson, bool Decline);

/// <summary>
/// The values a user is entering into an <see cref="McpQuestionForm"/>: per-field state, the
/// checks that can be made before sending, and the typed answer JSON. The bridge validates the
/// answer against the server's own schema again; these checks only spare the user a round trip.
/// One per rendered form (per browser tab), never shared.
/// </summary>
public sealed class McpQuestionFormModel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Dictionary<string, string?> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool?> _flags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _choices = new(StringComparer.Ordinal);

    public McpQuestionFormModel(McpQuestionForm form)
    {
        Form = form;
        foreach (var field in form.Fields)
        {
            switch (field.Kind)
            {
                case McpQuestionFieldKind.Boolean:
                    _flags[field.Name] = field.IsDecision ? null : ParseDefault<bool?>(field.Default);
                    break;
                case McpQuestionFieldKind.MultiSelect:
                    _choices[field.Name] = new HashSet<string>(
                        (ParseDefault<string[]>(field.Default) ?? [])
                            .Where(v => field.Options?.Any(o => o.Value == v) == true),
                        StringComparer.Ordinal);
                    break;
                case McpQuestionFieldKind.SingleSelect:
                    var choice = field.IsDecision ? null : ParseDefault<string>(field.Default);
                    _text[field.Name] = field.Options?.Any(o => o.Value == choice) == true ? choice : null;
                    break;
                case McpQuestionFieldKind.Number:
                case McpQuestionFieldKind.Integer:
                    _text[field.Name] = ParseDefault<double?>(field.Default)?.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    _text[field.Name] = ParseDefault<string>(field.Default);
                    break;
            }
        }
    }

    public McpQuestionForm Form { get; }

    /// <summary>The form carried in an <see cref="AgentReply.StructuredData"/>, or null if it can't be read.</summary>
    public static McpQuestionForm? TryParseForm(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var form = JsonSerializer.Deserialize<McpQuestionForm>(json, JsonOptions);
            return form is { QuestionId.Length: > 0 } ? form : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Field values ─────────────────────────────────────────────────────────

    /// <summary>A text, number or single-select field's value as entered.</summary>
    public string? GetText(string name) => _text.GetValueOrDefault(name);

    public void SetText(string name, string? value) => _text[name] = value;

    /// <summary>A boolean field's value; null until the user chooses.</summary>
    public bool? GetFlag(string name) => _flags.GetValueOrDefault(name);

    public void SetFlag(string name, bool? value) => _flags[name] = value;

    /// <summary>Whether a multi-select field has <paramref name="value"/> chosen.</summary>
    public bool IsChosen(string name, string value) =>
        _choices.TryGetValue(name, out var set) && set.Contains(value);

    public void SetChosen(string name, string value, bool chosen)
    {
        if (!_choices.TryGetValue(name, out var set))
            _choices[name] = set = new HashSet<string>(StringComparer.Ordinal);
        if (chosen)
            set.Add(value);
        else
            set.Remove(value);
    }

    // ── Checks and the answer ────────────────────────────────────────────────

    /// <summary>What's wrong with the values entered, by field name. Empty when the form can be sent.</summary>
    public IReadOnlyDictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Form.Fields)
        {
            if (Check(field) is { } error)
                errors[field.Name] = error;
        }

        return errors;
    }

    /// <summary>
    /// The answer as a JSON object keyed by the server's field names, with each value typed as
    /// the schema asks (string, number, boolean or string array). Fields left empty are omitted.
    /// Call <see cref="Validate"/> first.
    /// </summary>
    public string BuildAnswersJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var field in Form.Fields)
            {
                switch (field.Kind)
                {
                    case McpQuestionFieldKind.Boolean:
                        if (GetFlag(field.Name) is { } flag)
                            writer.WriteBoolean(field.Name, flag);
                        break;

                    case McpQuestionFieldKind.MultiSelect:
                        var chosen = field.Options?.Select(o => o.Value).Where(v => IsChosen(field.Name, v)).ToList() ?? [];
                        if (chosen.Count > 0 || field.Required)
                        {
                            writer.WriteStartArray(field.Name);
                            foreach (var value in chosen)
                                writer.WriteStringValue(value);
                            writer.WriteEndArray();
                        }
                        break;

                    case McpQuestionFieldKind.Integer:
                        if (long.TryParse(GetText(field.Name)?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                            writer.WriteNumber(field.Name, whole);
                        break;

                    case McpQuestionFieldKind.Number:
                        if (double.TryParse(GetText(field.Name)?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                            writer.WriteNumber(field.Name, number);
                        break;

                    default:
                        if (GetText(field.Name) is { Length: > 0 } text)
                            writer.WriteString(field.Name, text);
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private string? Check(McpQuestionField field)
    {
        switch (field.Kind)
        {
            case McpQuestionFieldKind.Boolean:
                return field.Required && GetFlag(field.Name) is null ? "Choose an answer." : null;

            case McpQuestionFieldKind.MultiSelect:
            {
                var count = _choices.GetValueOrDefault(field.Name)?.Count ?? 0;
                if (field.Required && count == 0)
                    return "Choose at least one.";
                if (count == 0)
                    return null;
                if (field.MinItems is { } min && count < min)
                    return $"Choose at least {min}.";
                if (field.MaxItems is { } max && count > max)
                    return $"Choose at most {max}.";
                return null;
            }

            case McpQuestionFieldKind.SingleSelect:
            {
                var value = GetText(field.Name);
                if (string.IsNullOrEmpty(value))
                    return field.Required ? "Choose an answer." : null;
                return field.Options?.Any(o => o.Value == value) == true ? null : "Choose one of the options.";
            }

            case McpQuestionFieldKind.Integer:
            case McpQuestionFieldKind.Number:
            {
                var text = GetText(field.Name)?.Trim();
                if (string.IsNullOrEmpty(text))
                    return field.Required ? "Enter a number." : null;

                double number;
                if (field.Kind == McpQuestionFieldKind.Integer)
                {
                    if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                        return "Enter a whole number.";
                    number = whole;
                }
                else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    return "Enter a number.";
                }

                if (field.Minimum is { } lo && number < lo)
                    return $"The minimum is {lo.ToString(CultureInfo.InvariantCulture)}.";
                if (field.Maximum is { } hi && number > hi)
                    return $"The maximum is {hi.ToString(CultureInfo.InvariantCulture)}.";
                return null;
            }

            default:
            {
                var text = GetText(field.Name) ?? "";
                if (text.Length == 0)
                    return field.Required ? "This is required." : null;
                if (field.MinLength is { } min && text.Length < min)
                    return $"Enter at least {min} characters.";
                if (field.MaxLength is { } max && text.Length > max)
                    return $"Enter at most {max} characters.";
                return CheckFormat(field.Format, text);
            }
        }
    }

    private static string? CheckFormat(string? format, string text) => format switch
    {
        "email" => text.Contains('@') && !text.StartsWith('@') && !text.EndsWith('@') ? null : "Enter an email address.",
        "uri" => Uri.TryCreate(text, UriKind.Absolute, out _) ? null : "Enter a full URL.",
        "date" => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? null
            : "Enter a date as YYYY-MM-DD.",
        "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? null
            : "Enter a date and time, such as 2026-10-12T14:30:00Z.",
        _ => null,
    };

    private static T? ParseDefault<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
