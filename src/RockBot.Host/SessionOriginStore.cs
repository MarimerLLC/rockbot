using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// In-memory cache of each user session's origin (channel + first-prompt summary + start
/// time), written on every inbound <see cref="UserMessage"/> arrival. Read by entry points
/// that emit <b>unsolicited</b> replies for a session but don't have the originating
/// <see cref="UserMessage"/> in scope — subagent result/progress handlers and A2A
/// receive-side handlers — so they can stamp <see cref="AgentReply.Origin"/> and let
/// frontends anchor the message to the request that started it.
/// <para>
/// Parallels <see cref="SessionClientCapabilityStore"/> in lifetime and intent: a singleton
/// spanning the agent process, persisting across loops (a subagent/A2A callback may fire long
/// after the user's loop completes), cleared on session reset. First-writer-wins per session
/// so follow-up turns don't overwrite the anchor of in-flight background work.
/// </para>
/// </summary>
public sealed class SessionOriginStore
{
    private readonly Dictionary<string, ReplyOrigin> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Records the origin for <paramref name="sessionId"/> if none is cached yet. The first
    /// turn of a session establishes the anchor; later turns within the same session do not
    /// overwrite it, so background work started early still reports its true beginning.
    /// </summary>
    public void Set(string sessionId, ReplyOrigin origin)
    {
        lock (_lock)
            _byId.TryAdd(sessionId, origin);
    }

    /// <summary>Returns the cached origin for <paramref name="sessionId"/>, or null.</summary>
    public ReplyOrigin? Get(string sessionId)
    {
        lock (_lock)
            return _byId.GetValueOrDefault(sessionId);
    }

    /// <summary>Removes any cached entry. Called when the user resets the conversation.</summary>
    public void Clear(string sessionId)
    {
        lock (_lock)
            _byId.Remove(sessionId);
    }
}
