using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using RockBot.Tools.Mcp.Elicitation;
using RockBot.UserProxy;

namespace RockBot.Agent.McpBridge.Handback;

/// <summary>
/// Turns a handed-back question into the <see cref="McpQuestionForm"/> a chat client renders, and
/// the plain-text rendering that goes with it for clients that can't. All server text is flattened
/// and capped here, so a client never has to trust it; it must still render it as text.
/// </summary>
internal static class McpQuestionFormMapper
{
    /// <summary>Longest question message kept in a form.</summary>
    public const int MessageMaxChars = 2000;

    /// <summary>Longest field title, description or option label kept in a form.</summary>
    public const int LabelMaxChars = 300;

    public static McpQuestionForm ToForm(PendingQuestion question)
    {
        var schema = question.Request.RequestedSchema;
        var required = schema?.Required is { Count: > 0 } r ? new HashSet<string>(r, StringComparer.Ordinal) : null;

        var fields = (schema?.Properties ?? new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>())
            .Where(p => p.Value is not null)
            .Select(p => ToField(p.Key, p.Value, required?.Contains(p.Key) == true))
            .ToList();

        return new McpQuestionForm
        {
            QuestionId = question.QuestionId,
            ServerName = Text(question.ServerName, LabelMaxChars),
            ToolName = Text(question.ToolName, LabelMaxChars),
            Message = Text(question.Request.Message is { Length: > 0 } m ? m : "(no question text)", MessageMaxChars),
            ExpiresAt = question.ExpiresAt,
            Fields = fields,
        };
    }

    /// <summary>
    /// The question as plain text: what a client that can't render the form shows, and what the
    /// chat history keeps.
    /// </summary>
    public static string ToText(McpQuestionForm form)
    {
        var builder = new StringBuilder()
            .Append("The MCP server \"").Append(form.ServerName).Append("\" asks, for ").Append(form.ToolName).Append(": ")
            .Append(form.Message);

        foreach (var field in form.Fields)
        {
            builder.Append("\n- ").Append(field.Title ?? field.Name);
            if (field.Options is { Count: > 0 } options)
                builder.Append(" (").Append(string.Join(", ", options.Select(o => o.Label))).Append(')');
            if (field.Required)
                builder.Append(", required");
        }

        return builder.ToString();
    }

    private static McpQuestionField ToField(string name, ElicitRequestParams.PrimitiveSchemaDefinition definition, bool required)
    {
        var isDecision = McpElicitationCoordinator.IsDecisionField(definition);
        var field = new McpQuestionField
        {
            // The name stays exact: it's the key the answer carries, and the bridge's validator
            // matches it. What's shown is the title, or the name flattened when there's no title.
            Name = name,
            Kind = McpQuestionFieldKind.String,
            Title = OptionalText(definition.Title) ?? Text(name, LabelMaxChars),
            Description = OptionalText(definition.Description),
            Required = required,
            IsDecision = isDecision,
        };

        field = definition switch
        {
            ElicitRequestParams.StringSchema s => field with
            {
                MinLength = s.MinLength,
                MaxLength = s.MaxLength,
                Format = OptionalText(s.Format),
                Default = DefaultOf(s.Default),
            },
            ElicitRequestParams.NumberSchema n => field with
            {
                Kind = string.Equals(n.Type, "integer", StringComparison.OrdinalIgnoreCase)
                    ? McpQuestionFieldKind.Integer
                    : McpQuestionFieldKind.Number,
                Minimum = n.Minimum,
                Maximum = n.Maximum,
                Default = DefaultOf(n.Default),
            },
            ElicitRequestParams.BooleanSchema b => field with
            {
                Kind = McpQuestionFieldKind.Boolean,
                Default = DefaultOf(b.Default),
            },
            ElicitRequestParams.UntitledSingleSelectEnumSchema e => field with
            {
                Kind = McpQuestionFieldKind.SingleSelect,
                Options = Options(e.Enum.Select(v => (v, (string?)null))),
                Default = DefaultOf(e.Default),
            },
            ElicitRequestParams.TitledSingleSelectEnumSchema e => field with
            {
                Kind = McpQuestionFieldKind.SingleSelect,
                Options = Options(e.OneOf.Select(o => (o.Const, (string?)o.Title))),
                Default = DefaultOf(e.Default),
            },
#pragma warning disable MCP9001 // deprecated by the spec, still emitted by older servers
            ElicitRequestParams.LegacyTitledEnumSchema e => field with
            {
                Kind = McpQuestionFieldKind.SingleSelect,
                Options = Options(e.Enum.Select((v, i) => (v, e.EnumNames is { } names && i < names.Count ? names[i] : null))),
                Default = DefaultOf(e.Default),
            },
#pragma warning restore MCP9001
            ElicitRequestParams.UntitledMultiSelectEnumSchema e => field with
            {
                Kind = McpQuestionFieldKind.MultiSelect,
                Options = Options(e.Items.Enum.Select(v => (v, (string?)null))),
                MinItems = e.MinItems,
                MaxItems = e.MaxItems,
                Default = DefaultOf(e.Default),
            },
            ElicitRequestParams.TitledMultiSelectEnumSchema e => field with
            {
                Kind = McpQuestionFieldKind.MultiSelect,
                Options = Options(e.Items.AnyOf.Select(o => (o.Const, (string?)o.Title))),
                MinItems = e.MinItems,
                MaxItems = e.MaxItems,
                Default = DefaultOf(e.Default),
            },
            _ => field,
        };

        // A decision is the user's to make: never preselect one, even if the server suggests it.
        return isDecision ? field with { Default = null } : field;
    }

    // Option values are what the answer carries back, so they stay as the server sent them (the
    // bridge's validator compares them exactly); only their labels are flattened.
    private static List<McpQuestionOption> Options(IEnumerable<(string Value, string? Title)> options) =>
        [.. options
            .Where(o => o.Value is not null)
            .Select(o => new McpQuestionOption(o.Value, Text(string.IsNullOrWhiteSpace(o.Title) ? o.Value : o.Title, LabelMaxChars)))];

    private static string? DefaultOf(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, value.GetType());

    private static string? OptionalText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Text(text, LabelMaxChars);

    private static string Text(string text, int max)
    {
        var flat = McpElicitationSchemaDescriber.Flatten(text);
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
