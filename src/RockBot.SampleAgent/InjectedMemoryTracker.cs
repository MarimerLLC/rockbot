using System.Collections.Concurrent;

namespace RockBot.SampleAgent;

/// <summary>
/// Tracks which long-term memory entry IDs have already been injected into each session's
/// LLM context, enabling delta injection: only entries the LLM has not yet seen are surfaced
/// on each turn. When the conversational topic drifts, newly relevant entries surface naturally
/// because they haven't been injected yet; already-seen entries are never re-injected.
///
/// Registered as a singleton. State is in-process and resets on restart (intentional — the
/// LLM's context window resets too, so re-injection on the next process start is correct).
/// </summary>
internal sealed class InjectedMemoryTracker
{
    // sessionId -> set of already-injected memory IDs
    // ConcurrentDictionary<string, byte> is the standard concurrent hash-set pattern.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessions = new();

    /// <summary>
    /// Attempts to mark <paramref name="memoryId"/> as injected for <paramref name="sessionId"/>.
    /// Returns <c>true</c> if this is the first injection of this ID (caller should inject it);
    /// returns <c>false</c> if it was already injected (caller should skip it).
    /// Thread-safe.
    /// </summary>
    public bool TryMarkAsInjected(string sessionId, string memoryId)
    {
        var set = _sessions.GetOrAdd(sessionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        return set.TryAdd(memoryId, 0);
    }

    /// <summary>
    /// Clears tracked state for a session, allowing all entries to be re-injected.
    /// Call this if the session is explicitly reset.
    /// </summary>
    public void Clear(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
