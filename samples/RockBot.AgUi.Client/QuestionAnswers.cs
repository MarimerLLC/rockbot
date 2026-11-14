using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RockBot.UserProxy;

namespace RockBot.AgUi.Client;

/// <summary>
/// The values a user enters into an <see cref="McpQuestionForm"/>, and the JSON answer built from
/// them: an object of field values keyed by field name, as <c>POST /questions/{id}/answer</c>
/// takes it. A view binds its inputs to <see cref="Text"/>, <see cref="Flags"/> and
/// <see cref="Choices"/>; the types on the wire are decided here, by each field's kind.
/// </summary>
public sealed class QuestionAnswers
{
    private readonly McpQuestionForm _form;

    public QuestionAnswers(McpQuestionForm form)
    {
        _form = form;
        foreach (var field in form.Fields)
        {
            var preset = ParseDefault(field.Default);
            switch (field.Kind)
            {
                case McpQuestionFieldKind.Boolean:
                    Flags[field.Name] = preset is { ValueKind: JsonValueKind.True };
                    break;
                case McpQuestionFieldKind.MultiSelect:
                    Choices[field.Name] = preset is { ValueKind: JsonValueKind.Array } list
                        ? [.. list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
                        : [];
                    break;
                default:
                    Text[field.Name] = preset switch
                    {
                        { ValueKind: JsonValueKind.String } text => text.GetString() ?? "",
                        { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
                        _ => "",
                    };
                    break;
            }
        }
    }

    /// <summary>What was typed or picked, for text, number and single-select fields.</summary>
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);

    /// <summary>Boolean fields.</summary>
    public Dictionary<string, bool> Flags { get; } = new(StringComparer.Ordinal);

    /// <summary>The values ticked in each multi-select field.</summary>
    public Dictionary<string, HashSet<string>> Choices { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the form is one yes/no decision, which a view can show as two buttons.</summary>
    public bool IsDecision => _form.Fields is [{ Kind: McpQuestionFieldKind.Boolean, IsDecision: true }];

    /// <summary>
    /// The answer, or what stops it being sent. The check here is only what a form can see for
    /// itself (a required field left empty, a number that isn't one); the MCP server's schema is
    /// enforced by the agent, which reports a refusal as the question's <c>rejected</c> state.
    /// </summary>
    public JsonObject? Build(out string? problem)
    {
        var answers = new JsonObject();
        foreach (var field in _form.Fields)
        {
            var label = string.IsNullOrEmpty(field.Title) ? field.Name : field.Title;
            switch (field.Kind)
            {
                case McpQuestionFieldKind.Boolean:
                    answers[field.Name] = Flags.GetValueOrDefault(field.Name);
                    break;

                case McpQuestionFieldKind.MultiSelect:
                    var chosen = Choices.GetValueOrDefault(field.Name) ?? [];
                    if (chosen.Count == 0 && field.Required)
                    {
                        problem = $"{label} is required.";
                        return null;
                    }
                    // In the order the form lists them, whatever order they were ticked in.
                    var ordered = (field.Options ?? []).Select(o => o.Value).Where(chosen.Contains);
                    answers[field.Name] = new JsonArray([.. ordered.Select(v => (JsonNode?)JsonValue.Create(v))]);
                    break;

                default:
                    var text = Text.GetValueOrDefault(field.Name)?.Trim() ?? "";
                    if (text.Length == 0)
                    {
                        if (field.Required)
                        {
                            problem = $"{label} is required.";
                            return null;
                        }
                        // An optional field left empty is left out, not sent as "".
                        break;
                    }

                    if (field.Kind is McpQuestionFieldKind.Number or McpQuestionFieldKind.Integer)
                    {
                        if (field.Kind == McpQuestionFieldKind.Integer
                            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                            answers[field.Name] = whole;
                        else if (field.Kind == McpQuestionFieldKind.Number
                            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                            answers[field.Name] = number;
                        else
                        {
                            problem = $"{label} must be a{(field.Kind == McpQuestionFieldKind.Integer ? " whole" : "")} number.";
                            return null;
                        }
                    }
                    else
                    {
                        answers[field.Name] = text;
                    }
                    break;
            }
        }

        problem = null;
        return answers;
    }

    /// <summary>A field's default is JSON text; one that doesn't parse is ignored.</summary>
    private static JsonElement? ParseDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
