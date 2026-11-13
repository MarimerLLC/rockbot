namespace RockBot.UserProxy;

/// <summary>
/// Reply from an agent back to the user.
/// </summary>
public sealed record AgentReply
{
    public required string Content { get; init; }
    public required string SessionId { get; init; }
    public required string AgentName { get; init; }
    public bool IsFinal { get; init; } = true;

    /// <summary>
    /// When true on a non-final reply, signals that the source has finished producing
    /// output and its activity log should be closed (spinner removed, header indicator
    /// cleared). Used by SubagentResultHandler to mark the Phase 1 completion bubble.
    /// </summary>
    public bool IsCompletion { get; init; }

    public string? StructuredData { get; init; }
    public string? ContentType { get; init; }

    /// <summary>
    /// Set on the agent's reply after the user answered a handed-back MCP question in its form
    /// (<see cref="McpQuestionAnswer"/>): that question's id. It lets a frontend tell the reply it
    /// is waiting for apart from the final reply of a turn that was still running when the form
    /// was submitted. Null on every other reply.
    /// </summary>
    public string? FollowsQuestionId { get; init; }

    /// <summary>
    /// Provenance for an unsolicited reply (subagent completion, scheduled task, A2A
    /// result, idle inbox batch). Null for replies that are a direct response to the
    /// user's current turn. Frontends render a short anchor preamble from this so the
    /// user can re-ground a message that finished after the originating session is gone
    /// or arrived in a different client.
    /// </summary>
    public ReplyOrigin? Origin { get; init; }

    /// <summary>
    /// Out-of-band binary payloads (images, etc.) the agent produced for this reply, carried
    /// as shared-volume path references rather than inline bytes. Only set on final replies —
    /// progress/non-final replies never carry attachments. Null when the reply is text-only.
    /// Capable frontends render these natively (Blazor serves them from an HTTP endpoint);
    /// others print a placeholder line. Serializes for free via the camelCase STJ path.
    /// </summary>
    public IReadOnlyList<AgentAttachment>? Attachments { get; init; }

    /// <summary>
    /// Structured form of a tool-progress reply: the call id, phase, arguments, result, duration
    /// and error. <see cref="Content"/> still carries the same information as text for frontends
    /// that don't read this. Null on every reply that isn't about a tool call.
    /// </summary>
    public ToolCallInfo? ToolCall { get; init; }

    /// <summary>
    /// Which part of the agent produced this reply (primary turn, subagent, worker, A2A,
    /// scheduled, bridge). Null from older agents; frontends then fall back to guessing from
    /// <see cref="AgentName"/> and <see cref="SessionId"/>.
    /// </summary>
    public ReplySource? Source { get; init; }
}
