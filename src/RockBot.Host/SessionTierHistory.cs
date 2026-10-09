namespace RockBot.Host;

/// <summary>
/// In-memory record of the tiers recent turns in each session were routed to, so a user
/// turn on an active thread can inherit the thread's tier instead of being scored on its
/// own text alone (#663). "do that" after a High analysis turn stays High; a Balanced deck
/// edit keeps the next short instruction on Balanced.
/// <para>
/// Bounded and expiring: each session keeps its <see cref="MaxTurnsPerSession"/> most recent
/// entries, entries older than <see cref="ShortMessageHeuristics.ThreadEstablishedRecency"/>
/// are ignored and pruned, and at most <see cref="MaxSessions"/> sessions are tracked (the
/// least recently touched is evicted). Losing it on a pod restart is fine — the next turns
/// rebuild it. Thread-safe.
/// </para>
/// <para>
/// Callers record a turn's <see cref="TierClassification.IntrinsicTier"/> — the tier it
/// earned on its own — not the inherited tier, so an inherited tier decays after
/// <see cref="MaxTurnsPerSession"/> turns that don't earn it rather than perpetuating itself.
/// A turn that escalates mid-loop raises its own entry via <see cref="Raise"/>.
/// </para>
/// </summary>
public sealed class SessionTierHistory(TimeProvider? timeProvider = null)
{
    /// <summary>Number of most-recent turns per session the inherited tier is drawn from.</summary>
    public const int MaxTurnsPerSession = 3;

    /// <summary>Maximum number of sessions tracked at once.</summary>
    public const int MaxSessions = 512;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, SessionEntries> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Records that turn <paramref name="turnId"/> of <paramref name="sessionId"/> routed to
    /// <paramref name="tier"/>. Recording the same turn again keeps the higher tier.
    /// </summary>
    public void Record(string sessionId, string turnId, ModelTier tier)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var entries))
            {
                if (_sessions.Count >= MaxSessions)
                    EvictLeastRecent();
                entries = new SessionEntries();
                _sessions[sessionId] = entries;
            }

            entries.LastTouched = now;
            var existing = entries.Turns.FindIndex(t => t.TurnId == turnId);
            if (existing >= 0)
            {
                var turn = entries.Turns[existing];
                entries.Turns[existing] = turn with { Tier = turn.Tier > tier ? turn.Tier : tier, At = now };
                return;
            }

            entries.Turns.Add(new TurnTier(turnId, tier, now));
            if (entries.Turns.Count > MaxTurnsPerSession)
                entries.Turns.RemoveRange(0, entries.Turns.Count - MaxTurnsPerSession);
        }
    }

    /// <summary>
    /// Raises the recorded tier of turn <paramref name="turnId"/> to at least
    /// <paramref name="tier"/> — used when a turn escalates mid-loop. Adds the turn when it
    /// is not recorded yet.
    /// </summary>
    public void Raise(string sessionId, string turnId, ModelTier tier) => Record(sessionId, turnId, tier);

    /// <summary>
    /// The highest tier among the session's recent turns (its last
    /// <see cref="MaxTurnsPerSession"/> entries within
    /// <see cref="ShortMessageHeuristics.ThreadEstablishedRecency"/>), or null when it has none.
    /// </summary>
    public ModelTier? GetRecentMax(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        var cutoff = _time.GetUtcNow() - ShortMessageHeuristics.ThreadEstablishedRecency;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sessionId, out var entries))
                return null;

            entries.Turns.RemoveAll(t => t.At < cutoff);
            if (entries.Turns.Count == 0)
            {
                _sessions.Remove(sessionId);
                return null;
            }

            return entries.Turns.Max(t => t.Tier);
        }
    }

    /// <summary>Forgets the session's history. Called when the user clears the conversation.</summary>
    public void Clear(string sessionId)
    {
        lock (_lock)
            _sessions.Remove(sessionId);
    }

    private void EvictLeastRecent()
    {
        string? oldestKey = null;
        var oldest = DateTimeOffset.MaxValue;
        foreach (var (key, entries) in _sessions)
        {
            if (entries.LastTouched < oldest)
            {
                oldest = entries.LastTouched;
                oldestKey = key;
            }
        }

        if (oldestKey is not null)
            _sessions.Remove(oldestKey);
    }

    private sealed record TurnTier(string TurnId, ModelTier Tier, DateTimeOffset At);

    private sealed class SessionEntries
    {
        public List<TurnTier> Turns { get; } = [];
        public DateTimeOffset LastTouched { get; set; }
    }
}
