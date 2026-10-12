namespace RockBot.UserProxy;

/// <summary>
/// A question an MCP server handed back for the user to answer, as a form. It travels in
/// <see cref="AgentReply.StructuredData"/> (JSON) with <see cref="AgentReply.ContentType"/> set to
/// <see cref="ContentType"/>, and only to clients that advertise
/// <see cref="ClientCapabilities.InteractiveForms"/>. The reply's <see cref="AgentReply.Content"/>
/// carries a plain-text rendering of the same question for anything that can't show the form.
/// </summary>
/// <remarks>
/// Every string here except <see cref="QuestionId"/> comes from the MCP server and is untrusted. The
/// agent flattens it before sending, and a frontend must still render it as text, never as markup.
/// The answer goes back as an <see cref="McpQuestionAnswer"/>; state changes arrive as
/// <see cref="McpQuestionStatus"/>.
/// </remarks>
public sealed record McpQuestionForm
{
    /// <summary><see cref="AgentReply.ContentType"/> of a reply that carries this form.</summary>
    public const string ContentType = "application/vnd.rockbot.mcp-question+json";

    public required string QuestionId { get; init; }
    public required string ServerName { get; init; }
    public required string ToolName { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public IReadOnlyList<McpQuestionField> Fields { get; init; } = [];
}

/// <summary>One field of an <see cref="McpQuestionForm"/>, from the server's schema.</summary>
public sealed record McpQuestionField
{
    /// <summary>The server's property name; the key of this field's value in the answer.</summary>
    public required string Name { get; init; }

    /// <summary>One of <see cref="McpQuestionFieldKind"/>.</summary>
    public required string Kind { get; init; }

    public string? Title { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }

    /// <summary>
    /// A yes/no choice only the user may make. The form offers no preselected answer for it.
    /// </summary>
    public bool IsDecision { get; init; }

    /// <summary>The choices of a single- or multi-select field.</summary>
    public IReadOnlyList<McpQuestionOption>? Options { get; init; }

    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }

    /// <summary>A string field's format hint: <c>email</c>, <c>uri</c>, <c>date</c> or <c>date-time</c>.</summary>
    public string? Format { get; init; }

    /// <summary>The server's default, as JSON text, or null.</summary>
    public string? Default { get; init; }
}

/// <summary>A choice of a select field. <see cref="Value"/> is what the answer carries.</summary>
public sealed record McpQuestionOption(string Value, string Label);

/// <summary>The kinds of <see cref="McpQuestionField"/>. Strings, so a new kind degrades rather than breaks.</summary>
public static class McpQuestionFieldKind
{
    public const string String = "string";
    public const string Number = "number";
    public const string Integer = "integer";
    public const string Boolean = "boolean";
    public const string SingleSelect = "single-select";
    public const string MultiSelect = "multi-select";
}
