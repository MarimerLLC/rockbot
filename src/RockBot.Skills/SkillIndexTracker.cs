using System.Collections.Concurrent;

namespace RockBot.Skills;

/// <summary>
/// Tracks which sessions have already received the skill index injection,
/// so it is only injected once per session rather than on every turn.
/// Registered as a singleton.
/// </summary>
public sealed class SkillIndexTracker
{
    private readonly ConcurrentDictionary<string, byte> _injectedSessions = new();

    /// <summary>
    /// Attempts to mark the session as having received the skill index.
    /// Returns <c>true</c> the first time for a given session (caller should inject);
    /// returns <c>false</c> on subsequent calls (already injected, skip).
    /// </summary>
    public bool TryMarkAsInjected(string sessionId) =>
        _injectedSessions.TryAdd(sessionId, 0);

    /// <summary>
    /// Clears the injected state for a session, allowing re-injection.
    /// Call this if the skill set changes mid-session and you want the next turn
    /// to re-inject the updated index automatically.
    /// </summary>
    public void Clear(string sessionId) => _injectedSessions.TryRemove(sessionId, out _);
}
