namespace RockBot.UserProxy;

/// <summary>
/// A change in the state of a handed-back MCP question shown as an <see cref="McpQuestionForm"/>.
/// Published to <see cref="UserProxyTopics.UserResponse"/> as its own message type, so frontends
/// can close or re-enable the form. A frontend ignores a <see cref="QuestionId"/> it doesn't know.
/// </summary>
public sealed record McpQuestionStatus
{
    public required string SessionId { get; init; }
    public required string QuestionId { get; init; }

    /// <summary>One of <see cref="McpQuestionState"/>.</summary>
    public required string State { get; init; }

    /// <summary>Who settled the question: <c>user</c> (the form) or <c>agent</c> (<c>mcp_answer</c>).</summary>
    public string? AnsweredBy { get; init; }

    /// <summary>Why, for <c>rejected</c>, <c>expired</c>, <c>cancelled</c> and <c>interrupted</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>States an <see cref="McpQuestionStatus"/> reports.</summary>
public static class McpQuestionState
{
    public const string Answered = "answered";
    public const string Declined = "declined";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";

    /// <summary>A form submission found the question no longer open (unknown, or already settled).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// The bridge refused a form submission (for example, it failed the server's schema). The
    /// question is still open; the form shows <see cref="McpQuestionStatus.Reason"/> and can be
    /// submitted again.
    /// </summary>
    public const string Rejected = "rejected";

    /// <summary>True when the question can no longer be answered.</summary>
    public static bool IsClosed(string? state) => state is not (null or Rejected);
}
