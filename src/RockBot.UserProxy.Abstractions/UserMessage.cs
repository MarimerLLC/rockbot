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
}
