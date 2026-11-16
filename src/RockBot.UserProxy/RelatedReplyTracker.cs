namespace RockBot.UserProxy;

/// <summary>
/// Remembers the unsolicited final replies this proxy passed to its frontend, so the next message
/// sent in a session can carry references to the ones from other sessions that the user saw there
/// (<see cref="UserMessage.RelatedReplies"/>). Without them a follow-up such as "summarize that",
/// typed in a client that showed another session's subagent result, reaches an agent session that
/// has never seen the result.
/// </summary>
/// <remarks>
/// Keeps the last <see cref="Capacity"/> replies, numbered in arrival order, and a cursor per
/// sending session: the number of the newest reply when that session last sent. A send takes the
/// replies after its session's cursor and moves the cursor on, so each reply is attached once per
/// session, and a session that hasn't sent for a while still gets what arrived meanwhile.
/// </remarks>
internal sealed class RelatedReplyTracker
{
    /// <summary>How many recent replies are remembered.</summary>
    internal const int Capacity = 50;

    /// <summary>At most this many references go on one message.</summary>
    internal const int MaxReferences = 5;

    private const string NamespacedSessionPrefix = "session/";

    private readonly Lock _lock = new();
    private readonly Queue<(long Seq, AgentReply Reply)> _recent = new();
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private long _seq;

    /// <summary>
    /// Remembers <paramref name="reply"/> if a later message could refer to it: a final reply the
    /// agent stored (it has a <see cref="AgentReply.MessageId"/>), and not an MCP question's form.
    /// </summary>
    public void Record(AgentReply reply)
    {
        if (!reply.IsFinal || string.IsNullOrEmpty(reply.MessageId) || string.IsNullOrEmpty(reply.SessionId))
            return;
        if (string.Equals(reply.ContentType, McpQuestionForm.ContentType, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_lock)
        {
            _recent.Enqueue((++_seq, reply));
            while (_recent.Count > Capacity)
                _recent.Dequeue();
        }
    }

    /// <summary>
    /// <paramref name="message"/> with references to the replies from other sessions that arrived
    /// since its session last sent and that <paramref name="shows"/> says were visible in it, the
    /// newest <see cref="MaxReferences"/>, after any the caller already set. Moves the session's
    /// cursor on, whether or not any were added.
    /// </summary>
    public UserMessage Stamp(UserMessage message, Func<string, AgentReply, bool> shows)
    {
        var sessionId = message.SessionId;
        List<AgentReply> candidates;
        lock (_lock)
        {
            var cursor = _cursors.GetValueOrDefault(sessionId);
            candidates = [.. _recent.Where(r => r.Seq > cursor).Select(r => r.Reply)];
            _cursors[sessionId] = _seq;
        }

        var found = new List<ReplyReference>();
        for (var i = candidates.Count - 1; i >= 0 && found.Count < MaxReferences; i--)
        {
            var reply = candidates[i];
            if (IsSameSession(reply.SessionId, sessionId))
                continue;
            if (!SafeShows(shows, sessionId, reply))
                continue;
            found.Add(new ReplyReference(Raw(reply.SessionId), reply.MessageId!));
        }

        if (found.Count == 0)
            return message;

        // Oldest first, after what the caller set; one reference per turn.
        found.Reverse();
        var merged = (message.RelatedReplies ?? []).Concat(found)
            .DistinctBy(r => (r.SessionId, r.MessageId))
            .ToList();
        return message with { RelatedReplies = merged };
    }

    private static bool SafeShows(Func<string, AgentReply, bool> shows, string sessionId, AgentReply reply)
    {
        try
        {
            return shows(sessionId, reply);
        }
        catch
        {
            // A frontend that can't say is treated as not having shown it; the message still goes.
            return false;
        }
    }

    private static bool IsSameSession(string replySessionId, string sessionId) =>
        string.Equals(Raw(replySessionId), Raw(sessionId), StringComparison.Ordinal);

    /// <summary>The session id without the agent's working-memory <c>session/</c> prefix.</summary>
    private static string Raw(string sessionId) =>
        sessionId.StartsWith(NamespacedSessionPrefix, StringComparison.Ordinal)
            ? sessionId[NamespacedSessionPrefix.Length..]
            : sessionId;
}
