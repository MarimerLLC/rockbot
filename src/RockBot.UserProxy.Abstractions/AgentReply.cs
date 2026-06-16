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
    /// Provenance for an unsolicited reply (subagent completion, scheduled task, A2A
    /// result, idle inbox batch). Null for replies that are a direct response to the
    /// user's current turn. Frontends render a short anchor preamble from this so the
    /// user can re-ground a message that finished after the originating session is gone
    /// or arrived in a different client.
    /// </summary>
    public ReplyOrigin? Origin { get; init; }
}
