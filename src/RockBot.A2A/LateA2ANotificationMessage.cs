namespace RockBot.A2A;

/// <summary>The kind of A2A reply that arrived late and is being folded back.</summary>
public enum NotificationKind
{
    Result,
    Status,
    Error,
    InputRequired
}

/// <summary>
/// Published when an A2A reply arrives for a subagent that has already exited. Rather than
/// dropping the reply, the receive-side handler stashes its payload to the primary session's
/// working memory and emits this message so the <c>LateA2ANotificationHandler</c> can fold it
/// into a fresh primary-agent turn.
/// </summary>
/// <remarks>
/// Going through the bus (rather than synthesizing inline in the A2A handler) lets multiple
/// late arrivals serialize into separate primary turns without colliding with an in-progress
/// turn, and lets the original A2A handler return promptly.
/// </remarks>
public sealed record LateA2ANotificationMessage(
    string PrimarySessionId,
    string SubagentTaskId,
    string SubagentName,
    string PeerAgent,
    NotificationKind Kind,
    string WorkingMemoryKey,
    string? RunOrigin = null,
    string? UserAskedFor = null)
{
    // RunOrigin / UserAskedFor: the consequential-action scope (#685) of the run whose work this
    // is, as ActionGateScope.LogName and CompletionEvalTriggers.LogName write them. The folding
    // turn runs under it. Null when unknown (A2A late replies don't carry it yet).

    /// <summary>
    /// Prefix of <see cref="PeerAgent"/> when the late reply is a background MCP task's, not an A2A
    /// peer's: <c>mcp:{server}</c>.
    /// </summary>
    public const string McpPeerPrefix = "mcp:";
}
