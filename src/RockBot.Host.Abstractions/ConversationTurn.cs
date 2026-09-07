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
}

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
