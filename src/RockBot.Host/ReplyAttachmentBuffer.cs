using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Session-keyed buffer of <see cref="AgentAttachment"/> the agent has staged for the next
/// final reply. The <c>attach_image</c> LLM tool calls <see cref="Add"/> during a turn; the
/// reply-publishing path calls <see cref="Drain"/> when it emits the final reply, moving the
/// staged attachments onto <see cref="AgentReply.Attachments"/> and clearing the buffer so
/// they aren't replayed on a later turn.
/// <para>
/// Mirrors <see cref="SessionClientCapabilityStore"/> in shape and lifetime: a process-wide
/// singleton holding short-lived per-session metadata, guarded by a single lock. Registered
/// as a singleton in the agent host.
/// </para>
/// </summary>
public sealed class ReplyAttachmentBuffer
{
    private readonly Dictionary<string, List<AgentAttachment>> _bySession
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Stage <paramref name="attachment"/> for <paramref name="sessionId"/>'s next final reply.
    /// </summary>
    public void Add(string sessionId, AgentAttachment attachment)
    {
        lock (_lock)
        {
            if (!_bySession.TryGetValue(sessionId, out var list))
                _bySession[sessionId] = list = new List<AgentAttachment>();
            list.Add(attachment);
        }
    }

    /// <summary>
    /// Returns and clears the attachments staged for <paramref name="sessionId"/>. Returns an
    /// empty list when nothing is staged. Drains so a later reply in the same session starts clean.
    /// </summary>
    public IReadOnlyList<AgentAttachment> Drain(string sessionId)
    {
        lock (_lock)
        {
            if (_bySession.Remove(sessionId, out var list))
                return list;
            return [];
        }
    }

    /// <summary>Drop any staged attachments for <paramref name="sessionId"/> without returning them.</summary>
    public void Clear(string sessionId)
    {
        lock (_lock)
            _bySession.Remove(sessionId);
    }
}
