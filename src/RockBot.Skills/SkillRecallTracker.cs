using System.Collections.Concurrent;

namespace RockBot.Skills;

/// <summary>
/// Tracks which skill names have already been surfaced via per-turn BM25 recall for each
/// session, enabling delta injection: only skills the LLM has not yet seen via the recall
/// path are injected on a given turn.
///
/// Registered as a singleton. State is in-process and resets on restart (intentional — the
/// LLM's context window resets too, so re-injection on the next process start is correct).
/// </summary>
public sealed class SkillRecallTracker
{
    // sessionId -> set of already-recalled skill names
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _sessions = new();

    /// <summary>
    /// Attempts to mark <paramref name="skillName"/> as recalled for <paramref name="sessionId"/>.
    /// Returns <c>true</c> if this is the first recall of this skill (caller should inject it);
    /// returns <c>false</c> if it was already recalled (caller should skip it).
    /// Thread-safe.
    /// </summary>
    public bool TryMarkAsRecalled(string sessionId, string skillName)
    {
        var set = _sessions.GetOrAdd(sessionId,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        return set.TryAdd(skillName, 0);
    }

    /// <summary>
    /// Clears tracked state for a session, allowing all skills to be re-recalled.
    /// Call this if the session is explicitly reset.
    /// </summary>
    public void Clear(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
