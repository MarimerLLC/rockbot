namespace RockBot.Host;

/// <summary>
/// Tracks which sessions have received the session-start briefing so it is
/// only presented once per session (on the first user turn).
/// Registered as a singleton.
/// </summary>
public sealed class SessionStartTracker
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Returns <c>true</c> the first time this session ID is seen (caller should run
    /// the session-start briefing); returns <c>false</c> on subsequent calls.
    /// </summary>
    public bool TryMarkAsFirstTurn(string sessionId)
    {
        lock (_lock)
            return _seen.Add(sessionId);
    }
}
