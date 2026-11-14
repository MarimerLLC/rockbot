using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// An <see cref="McpQuestionForm"/> as AG-UI sees it (#794, phase 3): the interrupt a run ends
/// with, and the <c>CUSTOM rockbot.mcp_question</c> event that carries the form. Pure, so it is
/// tested directly.
/// </summary>
internal static class McpQuestionInterrupts
{
    /// <summary>True for the reply that carries a question form.</summary>
    public static bool IsForm(AgentReply reply) =>
        string.Equals(reply.ContentType, McpQuestionForm.ContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>The form in a reply's <see cref="AgentReply.StructuredData"/>, or null when it isn't one.</summary>
    public static McpQuestionForm? TryParseForm(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var form = JsonSerializer.Deserialize<McpQuestionForm>(json, TurnEventMapper.PayloadJson);
            return form is { QuestionId.Length: > 0 } ? form : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The question as an AG-UI interrupt. Its id is the question's id, so a <c>resume</c> entry
    /// names the question it answers.
    /// </summary>
    public static AGUIInterrupt ToInterrupt(McpQuestionForm form) => new()
    {
        Id = form.QuestionId,
        Reason = Reason(form),
        Message = form.Message,
        ResponseSchema = ResponseSchema(form),
        // ISO 8601 in UTC, the convention the SDK documents for this field.
        ExpiresAt = form.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
        // What the schema can't say: which server and tool asked, and the form as the agent built
        // it (titles, decisions), for a client that knows RockBot.
        Metadata = JsonSerializer.SerializeToElement(
            new { rockbot = new { form.ServerName, form.ToolName, form } }, TurnEventMapper.PayloadJson),
    };

    /// <summary>
    /// <c>confirmation</c> for a single yes/no decision, which a client can show as two buttons;
    /// <c>input_required</c> for everything else.
    /// </summary>
    public static string Reason(McpQuestionForm form) =>
        form.Fields is [{ Kind: McpQuestionFieldKind.Boolean, IsDecision: true }]
            ? InterruptReasons.Confirmation
            : InterruptReasons.InputRequired;

    /// <summary>
    /// A JSON Schema for the answer: an object with one property per field, keyed by the server's
    /// field name. It is what a <c>resume</c> entry's <c>payload</c> (and
    /// <see cref="McpQuestionAnswer.Answers"/>) must look like. The MCP server's own schema is the
    /// one that is enforced, by the agent; this is rebuilt from the form for the client's benefit.
    /// </summary>
    public static JsonElement ResponseSchema(McpQuestionForm form)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var field in form.Fields)
        {
            // A server could repeat a name; the first wins rather than throwing.
            if (properties.ContainsKey(field.Name))
                continue;
            properties[field.Name] = FieldSchema(field);
            if (field.Required)
                required.Add(field.Name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        return JsonSerializer.SerializeToElement(schema);
    }

    private static JsonObject FieldSchema(McpQuestionField field)
    {
        var schema = new JsonObject();
        switch (field.Kind)
        {
            case McpQuestionFieldKind.String:
                schema["type"] = "string";
                Set(schema, "minLength", field.MinLength);
                Set(schema, "maxLength", field.MaxLength);
                if (!string.IsNullOrEmpty(field.Format))
                    schema["format"] = field.Format;
                break;

            case McpQuestionFieldKind.Number:
            case McpQuestionFieldKind.Integer:
                schema["type"] = field.Kind;
                Set(schema, "minimum", field.Minimum);
                Set(schema, "maximum", field.Maximum);
                break;

            case McpQuestionFieldKind.Boolean:
                schema["type"] = "boolean";
                break;

            case McpQuestionFieldKind.SingleSelect:
                schema["type"] = "string";
                AddChoices(schema, field.Options, "oneOf");
                break;

            case McpQuestionFieldKind.MultiSelect:
            {
                var items = new JsonObject { ["type"] = "string" };
                AddChoices(items, field.Options, "anyOf");
                schema["type"] = "array";
                schema["items"] = items;
                schema["uniqueItems"] = true;
                Set(schema, "minItems", field.MinItems);
                Set(schema, "maxItems", field.MaxItems);
                break;
            }

            // A kind from a newer agent: no constraint, so any value passes here and the agent decides.
        }

        if (!string.IsNullOrEmpty(field.Title))
            schema["title"] = field.Title;
        if (!string.IsNullOrEmpty(field.Description))
            schema["description"] = field.Description;
        if (ParseDefault(field.Default) is { } @default)
            schema["default"] = @default;
        return schema;
    }

    /// <summary>
    /// A select's choices: <c>enum</c> with the values, which every form generator reads, and,
    /// when a choice has a label of its own, the same values with titles (<c>const</c> +
    /// <c>title</c>, as MCP elicitation writes them).
    /// </summary>
    private static void AddChoices(JsonObject schema, IReadOnlyList<McpQuestionOption>? options, string titledKeyword)
    {
        if (options is not { Count: > 0 })
            return;

        schema["enum"] = new JsonArray([.. options.Select(o => (JsonNode?)JsonValue.Create(o.Value))]);
        if (options.Any(o => !string.Equals(o.Label, o.Value, StringComparison.Ordinal)))
        {
            schema[titledKeyword] = new JsonArray([.. options.Select(o =>
                (JsonNode?)new JsonObject { ["const"] = o.Value, ["title"] = o.Label })]);
        }
    }

    private static void Set(JsonObject schema, string keyword, double? value)
    {
        if (value is { } number)
            schema[keyword] = number;
    }

    private static void Set(JsonObject schema, string keyword, int? value)
    {
        if (value is { } number)
            schema[keyword] = number;
    }

    /// <summary>A field's default is JSON text; one that doesn't parse is left out.</summary>
    private static JsonNode? ParseDefault(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>CUSTOM rockbot.mcp_question</c>: the form, the same question as text, and the question as
    /// an interrupt, so a client working either way has what it needs from the one event.
    /// </summary>
    public static BaseEvent QuestionEvent(TurnEventMapper mapper, AgentReply reply, McpQuestionForm? form) =>
        mapper.Custom(TurnEventMapper.CustomNames.McpQuestion, new
        {
            reply.SessionId,
            reply.AgentName,
            // The form as the agent sent it, so a field this proxy doesn't know still arrives.
            form = ParseJson(reply.StructuredData),
            // The same question as plain text, for a client that can't show the form.
            text = reply.Content,
            interrupt = form is null ? (JsonElement?)null : InterruptElement(ToInterrupt(form)),
        });

    /// <summary>An interrupt in the SDK's own JSON, the shape <c>RUN_FINISHED</c> carries it in.</summary>
    public static JsonElement InterruptElement(AGUIInterrupt interrupt) =>
        JsonSerializer.SerializeToElement(interrupt, AGUIJsonSerializerContext.Default.AGUIInterrupt);

    private static JsonElement? ParseJson(string? json)
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
