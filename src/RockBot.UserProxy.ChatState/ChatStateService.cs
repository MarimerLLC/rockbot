namespace RockBot.UserProxy.ChatState;

/// <summary>
/// Holds a chat's state and tells the UI when it changes. <see cref="Apply"/> is the only way to
/// change it: every agent callback and user action becomes a <see cref="ChatEvent"/>, and
/// <see cref="ChatReducer"/> works out the new state.
/// </summary>
/// <remarks>
/// Safe to share across threads: events are applied one at a time, and <see cref="State"/> is an
/// immutable snapshot, so a render iterates it without locking and can't see a half-applied event.
/// </remarks>
public sealed class ChatStateService(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private ChatViewState _state = ChatViewState.Empty;

    /// <summary>The current state.</summary>
    public ChatViewState State => Volatile.Read(ref _state);

    /// <summary>Raised after an event changes the state, on the thread that applied it.</summary>
    public event Action? OnStateChanged;

    /// <summary>The current time, in UTC, by the clock this store stamps events with.</summary>
    public DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>True while a turn is running or a form answer is waiting for its follow-up.</summary>
    public bool IsBusy => State.IsBusy(UtcNow);

    /// <summary>
    /// Applies an event, stamping it with the current time when it has none.
    /// </summary>
    /// <returns>False when the event was refused, for example an answer to a closed form.</returns>
    public bool Apply(ChatEvent chatEvent)
    {
        if (chatEvent.Timestamp == default)
            chatEvent = chatEvent with { Timestamp = UtcNow };

        ChatReduction result;
        bool changed;
        lock (_lock)
        {
            result = ChatReducer.Apply(_state, chatEvent);
            changed = !ReferenceEquals(result.State, _state);
            if (changed)
                Volatile.Write(ref _state, result.State);
        }

        if (changed)
            OnStateChanged?.Invoke();
        return result.Accepted;
    }
}
