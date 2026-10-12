namespace RockBot.UserProxy;

/// <summary>
/// The user's answer to an <see cref="McpQuestionForm"/>, published fire-and-forget on
/// <see cref="UserProxyTopics.McpAnswer"/>. The agent records it as the user's turn, hands it to the
/// MCP bridge (same session, the server's schema, one-shot), and replies once the resumed call
/// finishes. A refusal comes back as an <see cref="McpQuestionStatus"/> with state
/// <see cref="McpQuestionState.Rejected"/>.
/// </summary>
public sealed record McpQuestionAnswer
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public required string QuestionId { get; init; }

    /// <summary>A JSON object of field values keyed by the server's field names. Ignored on decline.</summary>
    public string? Answers { get; init; }

    /// <summary>True when the user refused the question; the server continues without the values.</summary>
    public bool Decline { get; init; }
}
