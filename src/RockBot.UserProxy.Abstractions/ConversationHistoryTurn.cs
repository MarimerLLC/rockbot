namespace RockBot.UserProxy;

/// <summary>
/// A single turn in a replayed conversation history.
/// </summary>
public sealed record ConversationHistoryTurn
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The name of the agent that produced this turn, if applicable.
    /// Used by the UI to restore proper message categorization on history reload.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Files attached to this turn, as path references. Lets a reloaded session render the
    /// image the user sent three turns ago instead of silently dropping it.
    /// </summary>
    public IReadOnlyList<AgentAttachment>? Attachments { get; init; }

    /// <summary>
    /// Stable id of the turn. Equal to <see cref="AgentReply.MessageId"/> on the live reply it was
    /// stored from, so a frontend can match the two. Null from older agents.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// The session the turn belongs to: the requested one, or a broadcast session
    /// (<c>scheduled</c>, <c>scheduled-system</c>, <c>a2a-inbound</c>) when the request set
    /// <see cref="ConversationHistoryRequest.IncludeBroadcast"/>. Null from older agents.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Provenance of an unsolicited turn, as <see cref="AgentReply.Origin"/> had it live. Frontends
    /// render the same anchor from it on reload.
    /// </summary>
    public ReplyOrigin? Origin { get; init; }

    /// <summary>
    /// Which part of the agent produced the turn, as <see cref="AgentReply.Source"/> had it live.
    /// Null for the user's own turns and from older agents; frontends then fall back to
    /// <see cref="AgentName"/>.
    /// </summary>
    public ReplySource? Source { get; init; }

    /// <summary>
    /// Set on a copy of a reply from another session, made because the user replied to it from
    /// this one (#841): the session the original was in. <see cref="SessionId"/> stays the session
    /// the copy is stored in; a frontend renders the anchor as for a reply from this session. Null
    /// on every other turn.
    /// </summary>
    public string? ImportedFromSessionId { get; init; }
}
