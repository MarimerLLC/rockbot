namespace RockBot.UserProxy;

/// <summary>
/// Message sent from a human user to the agent bus.
/// </summary>
public sealed record UserMessage
{
    public required string Content { get; init; }
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public string? TargetAgent { get; init; }

    /// <summary>
    /// Rendering capabilities of the client originating this message. The agent
    /// uses this to scope the rich-content subset it emits on the reply, and
    /// caches the value per-session so other entry points (A2A callbacks,
    /// subagent runs) producing replies for the same session can also honour it.
    /// Default <see cref="ClientCapabilities.None"/> falls through to the agent's
    /// markdown-only behaviour — older proxies that don't set this field keep
    /// working unchanged.
    /// </summary>
    public ClientCapabilities ClientCapabilities { get; init; } = ClientCapabilities.None;

    /// <summary>
    /// Human-friendly channel name of the originating client ("cli", "blazor", "discord", …).
    /// Cached per-session so unsolicited replies (subagent/A2A/scheduled) can show the user
    /// where the originating request came from. Null for older proxies that don't set it —
    /// the agent falls back to deriving the channel from the envelope source.
    /// </summary>
    public string? ChannelName { get; init; }

    /// <summary>
    /// Files the user attached to this message, as shared-volume path references rather than
    /// inline bytes — the same <see cref="AgentAttachment"/> shape the outbound half uses. The
    /// bytes reached the volume through <see cref="AttachmentUploadRequest"/> before this
    /// message was sent, so nothing here is large. Null for text-only messages.
    /// </summary>
    public IReadOnlyList<AgentAttachment>? Attachments { get; init; }

    /// <summary>
    /// Replies from other sessions that the sending client showed the user since its last message
    /// in this session (a subagent result, a scheduled task's output), so the user may be replying
    /// to one of them. The agent copies each into this session before answering, so it has the
    /// context. The user proxy fills this in (<c>UserProxyService</c>); a client may also set it.
    /// Null when there are none, and from older proxies.
    /// </summary>
    public IReadOnlyList<ReplyReference>? RelatedReplies { get; init; }
}
