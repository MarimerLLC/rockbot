using RockBot.UserProxy;

namespace RockBot.Host;

/// <summary>
/// Per-turn buffer of <see cref="AgentAttachment"/> the agent has staged for an upcoming final
/// reply. The <c>attach_image</c> LLM tool calls <see cref="Add"/> during a turn; the
/// reply-publishing path calls <see cref="DrainForFinalReply"/> (or <see cref="Drain"/>) when it
/// emits the final reply, moving the staged attachments onto <see cref="AgentReply.Attachments"/>
/// and clearing the turn's stage so they aren't replayed on a later turn.
/// <para>
/// Keyed two levels deep: <c>session → turn</c>. Per-turn keying is what makes the buffer safe
/// when more than one producer is in flight for the same primary session concurrently (e.g. an
/// A2A result and a subagent completion). A session-only key let whichever producer published
/// first scoop up <em>every</em> staged attachment and land them on the wrong bubble; per-turn
/// keying scopes each drain to its own producer.
/// </para>
/// <para>
/// Staged turns carry a <see cref="DateTimeOffset"/> stamp and expire after a TTL (default 30
/// minutes — comfortably above any loop duration so legitimate long in-flight stages are never
/// swept). Expired turns are swept lazily at the top of every public method. A process-wide
/// singleton holding short-lived per-session metadata, guarded by a single lock; mirrors
/// <see cref="SessionClientCapabilityStore"/> in shape and lifetime.
/// </para>
/// </summary>
public sealed class ReplyAttachmentBuffer
{
    /// <summary>Default time after which an un-drained staged turn is swept.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(30);

    private sealed class StagedTurn
    {
        public List<AgentAttachment> Items { get; } = new();
        public DateTimeOffset LastStagedAt { get; set; }
    }

    private readonly Dictionary<string, Dictionary<string, StagedTurn>> _bySession
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;

    public ReplyAttachmentBuffer(TimeProvider? timeProvider = null, TimeSpan? ttl = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? DefaultTtl;
    }

    /// <summary>
    /// Stage <paramref name="attachment"/> for the (<paramref name="sessionId"/>,
    /// <paramref name="turnId"/>) turn's next final reply. Refreshes the turn's TTL stamp.
    /// </summary>
    public void Add(string sessionId, string turnId, AgentAttachment attachment)
    {
        lock (_lock)
        {
            Sweep();
            if (!_bySession.TryGetValue(sessionId, out var turns))
                _bySession[sessionId] = turns = new Dictionary<string, StagedTurn>(StringComparer.OrdinalIgnoreCase);
            if (!turns.TryGetValue(turnId, out var staged))
                turns[turnId] = staged = new StagedTurn();
            staged.Items.Add(attachment);
            staged.LastStagedAt = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// Returns and clears the attachments staged for the (<paramref name="sessionId"/>,
    /// <paramref name="turnId"/>) turn. Returns an empty list when nothing is staged. Drains so a
    /// later reply in the same turn starts clean.
    /// </summary>
    public IReadOnlyList<AgentAttachment> Drain(string sessionId, string turnId)
    {
        lock (_lock)
        {
            Sweep();
            if (_bySession.TryGetValue(sessionId, out var turns)
                && turns.Remove(turnId, out var staged))
            {
                if (turns.Count == 0)
                    _bySession.Remove(sessionId);
                return staged.Items;
            }
            return [];
        }
    }

    /// <summary>
    /// The shared drain seam every reply-publish site calls. Returns <c>null</c> when
    /// <paramref name="isFinal"/> is false (non-final replies never carry attachments) or when
    /// nothing is staged for the turn; otherwise returns the drained list.
    /// </summary>
    public IReadOnlyList<AgentAttachment>? DrainForFinalReply(string sessionId, string turnId, bool isFinal)
    {
        if (!isFinal)
            return null;
        var drained = Drain(sessionId, turnId);
        return drained.Count > 0 ? drained : null;
    }

    /// <summary>Drop all staged attachments for every turn of <paramref name="sessionId"/> (ClearContext / session cancel).</summary>
    public void Clear(string sessionId)
    {
        lock (_lock)
        {
            Sweep();
            _bySession.Remove(sessionId);
        }
    }

    /// <summary>Drop the staged attachments for one (<paramref name="sessionId"/>, <paramref name="turnId"/>) turn, leaving sibling turns intact (per-turn cancel cleanup).</summary>
    public void Clear(string sessionId, string turnId)
    {
        lock (_lock)
        {
            Sweep();
            if (_bySession.TryGetValue(sessionId, out var turns)
                && turns.Remove(turnId)
                && turns.Count == 0)
            {
                _bySession.Remove(sessionId);
            }
        }
    }

    /// <summary>
    /// Removes staged turns whose last stage is older than the TTL, pruning emptied session
    /// dictionaries. Caller must hold <see cref="_lock"/>. Cardinality is tiny, so the cost is
    /// negligible.
    /// </summary>
    private void Sweep()
    {
        var now = _time.GetUtcNow();
        List<string>? emptySessions = null;
        foreach (var (sessionId, turns) in _bySession)
        {
            List<string>? expired = null;
            foreach (var (turnId, staged) in turns)
            {
                if (now - staged.LastStagedAt > _ttl)
                    (expired ??= new List<string>()).Add(turnId);
            }
            if (expired is not null)
            {
                foreach (var turnId in expired)
                    turns.Remove(turnId);
            }
            if (turns.Count == 0)
                (emptySessions ??= new List<string>()).Add(sessionId);
        }
        if (emptySessions is not null)
        {
            foreach (var sessionId in emptySessions)
                _bySession.Remove(sessionId);
        }
    }
}
