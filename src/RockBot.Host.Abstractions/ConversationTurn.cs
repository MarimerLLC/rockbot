namespace RockBot.Host;

/// <summary>
/// A single turn in a conversation session.
/// </summary>
/// <param name="Role">The role of the participant (e.g. "user", "assistant").</param>
/// <param name="Content">The content of the turn.</param>
/// <param name="Timestamp">When the turn occurred.</param>
public sealed record ConversationTurn(string Role, string Content, DateTimeOffset Timestamp)
{
    /// <summary>
    /// The name of the agent that produced this turn, if applicable.
    /// Used to restore proper UI categorization (subagent, A2A, primary) on history reload.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Files attached to this turn, as shared-volume path references. Additive by the policy in
    /// <c>design/schema-migrations.md</c> — the tolerant deserializer absorbs it and the
    /// conversation store is not enrolled in schema migrations, so no version bump is needed.
    /// </summary>
    public IReadOnlyList<ConversationTurnAttachment>? Attachments { get; init; }

    /// <summary>
    /// Stable id of the turn. The conversation store assigns one when the writer leaves it null; a
    /// writer that also publishes the turn as a reply sets it to the reply's
    /// <c>AgentReply.MessageId</c>, so a frontend can match the live message with its copy in
    /// history. Additive, like <see cref="Attachments"/>.
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// Provenance of an unsolicited turn (a subagent, A2A or scheduled result): the request that
    /// started the work, mirroring the user-proxy <c>ReplyOrigin</c>. Null for the user's own turns
    /// and the agent's direct replies.
    /// </summary>
    public ConversationTurnOrigin? Origin { get; init; }

    /// <summary>
    /// Which part of the agent produced the turn, mirroring the user-proxy <c>ReplySource</c>. Null
    /// for the user's own turns and for turns stored before it was recorded.
    /// </summary>
    public ConversationTurnSource? Source { get; init; }

    /// <summary>
    /// Set on a copy of a turn from another session, made because the user was shown that turn in
    /// the client they are now writing from (#841): the session and id of the original. Null on
    /// every other turn. Additive, like <see cref="Attachments"/>.
    /// </summary>
    public ConversationTurnReference? ImportedFrom { get; init; }

    /// <summary>
    /// Kept in the conversation's history but left out of the conversation log the dream and
    /// observation passes learn from (#853): a turn that isn't the user's own words although its
    /// role is <c>user</c>, such as the bridge's note that a watched resource changed. Its text is a
    /// server's data plus the user's watch instructions, and learning a skill or memory from it
    /// would put a second, drifting copy of those instructions beside the user's own. The agent's
    /// reply to such a turn is marked too, as it tends to restate them. Additive.
    /// </summary>
    public bool ExcludeFromLearning { get; init; }

    /// <summary>
    /// A new value for <see cref="Id"/>. A writer that publishes the turn as a reply uses the same
    /// value for the reply's message id.
    /// </summary>
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Mirror of the user-proxy <c>ReplyOrigin</c> on a <see cref="ConversationTurn"/>, declared here for
/// the same reason as <see cref="ConversationTurnAttachment"/>.
/// </summary>
/// <param name="Channel">Originating channel, e.g. "blazor", "scheduled", "a2a-inbound".</param>
/// <param name="PromptSummary">Truncated first user turn that started the work.</param>
/// <param name="StartedAt">When the originating request started.</param>
/// <param name="SessionId">Original session id.</param>
public sealed record ConversationTurnOrigin(
    string Channel,
    string PromptSummary,
    DateTimeOffset StartedAt,
    string? SessionId);

/// <summary>
/// Mirror of the user-proxy <c>ReplySource</c> on a <see cref="ConversationTurn"/>.
/// </summary>
/// <param name="Kind">One of the user-proxy <c>ReplySourceKind</c> values (primary, subagent, worker, a2a, scheduled, bridge).</param>
/// <param name="TaskId">The subagent's or worker's task id, when there is one.</param>
public sealed record ConversationTurnSource(string Kind, string? TaskId = null);

/// <summary>A turn in another session: <see cref="ConversationTurn.ImportedFrom"/>.</summary>
/// <param name="SessionId">The session the original turn is in.</param>
/// <param name="Id">The original turn's <see cref="ConversationTurn.Id"/>.</param>
public sealed record ConversationTurnReference(string SessionId, string Id);

/// <summary>
/// A file attached to a <see cref="ConversationTurn"/>, mirroring the user-proxy
/// <c>AgentAttachment</c> shape. Declared here rather than reused because
/// <c>RockBot.Host.Abstractions</c> does not reference the user-proxy contracts, and a
/// dependency in that direction would drag the whole proxy vocabulary into the host.
/// </summary>
/// <param name="Mime">MIME type of the file (e.g. <c>image/png</c>).</param>
/// <param name="Path">Relative path under the shared attachments directory.</param>
/// <param name="FileName">Optional friendly display name.</param>
public sealed record ConversationTurnAttachment(string Mime, string Path, string? FileName = null);
