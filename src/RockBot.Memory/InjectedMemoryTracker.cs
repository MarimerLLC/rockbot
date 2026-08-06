using System.Collections.Concurrent;

namespace RockBot.Memory;

/// <summary>
/// Tracks which long-term memory entry IDs have already been injected into each session's
/// LLM context, enabling delta injection: only entries the LLM has not yet seen are surfaced
/// on each turn. When the conversational topic drifts, newly relevant entries surface naturally
/// because they haven't been injected yet.
///
/// Injections expire. A recalled memory is appended as a system message for a single LLM call
/// and is never written into conversation history, so the only durable trace it leaves is
/// whatever the assistant's reply happened to carry forward. Once the turn it was injected on
/// scrolls out of the history window the model can still see, the memory is absent from context
/// entirely — suppressing re-injection past that point would make it permanently unrecallable
/// even while it keeps ranking top of every search. Callers pass the current turn index and the
/// size of the visible history window; entries injected longer ago than that window become
/// eligible for re-injection.
///
/// Registered as a singleton. State is in-process and resets on restart (intentional — the
/// LLM's context window resets too, so re-injection on the next process start is correct).
/// Callers that wipe a session's conversation history must also <see cref="Clear"/> it, or the
/// session resumes with no history and its memories still suppressed.
/// </summary>
public sealed class InjectedMemoryTracker
{
    // sessionId -> (memoryId -> turn index at which it was last injected)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _sessions = new();

    /// <summary>
    /// Attempts to mark <paramref name="memoryId"/> as injected for <paramref name="sessionId"/>.
    /// Returns <c>true</c> if the caller should inject it — either because it has never been
    /// injected, or because the turn that carried it has scrolled out of the visible window.
    /// Returns <c>false</c> if it is still present in context and should be skipped.
    /// Thread-safe.
    /// </summary>
    /// <param name="sessionId">Session whose context is being built.</param>
    /// <param name="memoryId">ID of the memory entry being considered for injection.</param>
    /// <param name="currentTurn">
    /// Index of the turn being built — the count of turns in conversation memory. Only meaningful
    /// alongside <paramref name="visibleHistoryTurns"/>.
    /// </param>
    /// <param name="visibleHistoryTurns">
    /// How many of the most recent turns the model can still see. An entry injected more than this
    /// many turns ago is treated as no longer present in context and becomes injectable again.
    /// <c>null</c> disables expiry, injecting an entry at most once per session.
    /// </param>
    public bool TryMarkAsInjected(
        string sessionId,
        string memoryId,
        int currentTurn = 0,
        int? visibleHistoryTurns = null)
    {
        var set = _sessions.GetOrAdd(sessionId,
            _ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal));

        while (true)
        {
            if (set.TryAdd(memoryId, currentTurn))
                return true;

            if (!set.TryGetValue(memoryId, out var injectedAtTurn))
                continue; // Removed concurrently — retry the add.

            if (visibleHistoryTurns is not int window || currentTurn - injectedAtTurn < window)
                return false;

            // The turn carrying this memory has scrolled out of the window the model can see;
            // re-injecting is the only way it can observe this entry again.
            // A lost race means another thread just re-injected it for this same turn.
            return set.TryUpdate(memoryId, currentTurn, injectedAtTurn);
        }
    }

    /// <summary>
    /// Clears tracked state for a session, allowing all entries to be re-injected.
    /// Call this whenever the session's conversation history is reset or wiped.
    /// </summary>
    public void Clear(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
