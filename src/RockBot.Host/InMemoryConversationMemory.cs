using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Ephemeral in-memory conversation memory with sliding window eviction and idle session cleanup.
/// </summary>
internal sealed class InMemoryConversationMemory : IConversationMemory, IDisposable
{
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();
    private readonly ConversationMemoryOptions _options;
    private readonly ILogger<InMemoryConversationMemory> _logger;
    private readonly Timer _cleanupTimer;

    public InMemoryConversationMemory(
        IOptions<ConversationMemoryOptions> options,
        ILogger<InMemoryConversationMemory> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Run cleanup every minute
        _cleanupTimer = new Timer(
            CleanupIdleSessions,
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    public Task AddTurnAsync(string sessionId, ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        AddTurn(sessionId, turn);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records a turn and reports what that did to the session: whether the turn started it
    /// (nothing was held for <paramref name="sessionId"/>, including after idle expiry), and the
    /// turns evicted past <see cref="ConversationMemoryOptions.MaxTurnsPerSession"/>, oldest first.
    /// </summary>
    internal (bool Started, IReadOnlyList<ConversationTurn> Evicted) AddTurn(string sessionId, ConversationTurn turn)
    {
        var started = false;
        var session = _sessions.GetOrAdd(sessionId, _ =>
        {
            started = true;
            return new SessionData();
        });
        List<ConversationTurn>? evicted = null;

        // Every stored turn has an id (#809), so history can be matched with what a client saw.
        if (string.IsNullOrEmpty(turn.Id))
            turn = turn with { Id = ConversationTurn.NewId() };

        lock (session.Lock)
        {
            session.Turns.AddLast(turn);
            session.LastAccessed = DateTimeOffset.UtcNow;

            // Sliding window: evict oldest turns when limit exceeded (O(1) removal)
            while (session.Turns.Count > _options.MaxTurnsPerSession)
            {
                (evicted ??= []).Add(session.Turns.First!.Value);
                session.Turns.RemoveFirst();
            }
        }

        return (started, evicted ?? (IReadOnlyList<ConversationTurn>)[]);
    }

    /// <summary>Whether <paramref name="sessionId"/> is currently held (not cleared or expired).</summary>
    internal bool HasSession(string sessionId) => _sessions.ContainsKey(sessionId);

    public Task<IReadOnlyList<ConversationTurn>> GetTurnsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return Task.FromResult<IReadOnlyList<ConversationTurn>>(Array.Empty<ConversationTurn>());

        lock (session.Lock)
        {
            session.LastAccessed = DateTimeOffset.UtcNow;
            return Task.FromResult<IReadOnlyList<ConversationTurn>>([.. session.Turns]);
        }
    }

    public Task ClearAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(sessionId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([.. _sessions.Keys]);

    private void CleanupIdleSessions(object? state)
    {
        var cutoff = DateTimeOffset.UtcNow - _options.SessionIdleTimeout;
        var removed = 0;

        foreach (var kvp in _sessions)
        {
            // Broadcast sessions don't idle out (#809): they're merged into every thread's history,
            // so they must last as long as the threads do, not expire an hour after the last
            // report while the user is still chatting. MaxTurnsPerSession bounds them.
            if (kvp.Value.LastAccessed < cutoff && !WellKnownSessions.IsBroadcast(kvp.Key))
            {
                if (_sessions.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogDebug("Cleaned up {Count} idle conversation sessions", removed);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }

    private sealed class SessionData
    {
        public readonly object Lock = new();
        public readonly LinkedList<ConversationTurn> Turns = new();
        public DateTimeOffset LastAccessed = DateTimeOffset.UtcNow;
    }
}
