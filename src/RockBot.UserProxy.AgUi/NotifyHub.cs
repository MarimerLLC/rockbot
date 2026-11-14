using System.Globalization;
using System.Threading.Channels;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>One server-sent event of the notify stream: its <c>id</c> and its <c>data</c>, an AG-UI event as JSON.</summary>
internal readonly record struct NotifyFrame(string Id, string Json);

/// <summary>
/// The notify stream's state (#794, phase 2): for each allowed thread, a sequence of events, a
/// bounded buffer of the latest ones, and the connections reading them.
/// </summary>
/// <remarks>
/// <para>
/// Events are published from the bus's message-handling thread, and connections come and go on
/// request threads. Each thread has one lock, held only for in-memory work: mapping an event,
/// numbering and buffering it, and a non-blocking write to each subscriber's queue. Writing to a
/// client happens on that client's own request, so a slow or dead client can't delay the bus or
/// anyone else. Its queue fills up instead, and then it is dropped: its stream ends, and it
/// reconnects with <c>Last-Event-ID</c> to get what it missed from the buffer.
/// </para>
/// <para>
/// An event's id is <c>{epoch}-{sequence}</c>. The sequence counts up from 1 for each thread. The
/// epoch is random for each hub (so, for each process): an id from before a restart, or from
/// another replica, never matches this hub's and can't be mistaken for a position in its sequence.
/// </para>
/// </remarks>
internal sealed class NotifyHub
{
    /// <summary><c>rockbot.resync</c> reasons.</summary>
    public static class ResyncReasons
    {
        /// <summary>The id is from this stream, but the events after it are no longer buffered.</summary>
        public const string Expired = "expired";

        /// <summary>The id is not from this stream: the proxy restarted, or another replica issued it.</summary>
        public const string Unknown = "unknown";
    }

    private readonly Dictionary<string, NotifyThread> _threads = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public NotifyHub(AgUiOptions options, TimeProvider? clock = null, ILogger<NotifyHub>? logger = null, string? epoch = null)
    {
        _logger = logger ?? (ILogger)Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Epoch = epoch ?? Guid.NewGuid().ToString("N")[..8];
        foreach (var threadId in options.ThreadIds())
            _threads[threadId] = new NotifyThread(threadId, Epoch, options, clock ?? TimeProvider.System, _logger);
    }

    /// <summary>What tells this hub's event ids from another's.</summary>
    public string Epoch { get; }

    /// <summary>
    /// Publishes to the threads a reply's session belongs to: the thread with that id (in either
    /// form, see <see cref="SessionRouting.IsSessionsReply"/>), or every thread for a session that
    /// belongs to no one conversation. Anything else is some other client's and is dropped.
    /// </summary>
    public void PublishToSession(string? sessionId, Func<TurnEventMapper, IEnumerable<BaseEvent>> map)
    {
        if (SessionRouting.IsBroadcastSession(sessionId))
        {
            PublishToAll(map);
            return;
        }

        var matched = false;
        foreach (var thread in _threads.Values)
        {
            if (!SessionRouting.IsSessionsReply(thread.ThreadId, sessionId))
                continue;
            matched = true;
            thread.Publish(map);
        }

        if (!matched)
            _logger.LogDebug("Dropping an event for session {SessionId}: not one of this proxy's threads", sessionId);
    }

    /// <summary>
    /// Publishes to the thread with exactly this id, for the events that carry a <c>ThreadId</c>
    /// (the agent always sends the raw session id there).
    /// </summary>
    public void PublishToThread(string? threadId, Func<TurnEventMapper, IEnumerable<BaseEvent>> map)
    {
        if (threadId is not null && _threads.TryGetValue(threadId, out var thread))
            thread.Publish(map);
        else
            _logger.LogDebug("Dropping an event for thread {ThreadId}: not one of this proxy's threads", threadId);
    }

    /// <summary>Publishes to every thread, for what belongs to no session (the agent's name, sign-ins, errors).</summary>
    public void PublishToAll(Func<TurnEventMapper, IEnumerable<BaseEvent>> map)
    {
        foreach (var thread in _threads.Values)
            thread.Publish(map);
    }

    /// <summary>
    /// Starts reading a thread's events. Without <paramref name="lastEventId"/> the subscription
    /// starts with a <c>rockbot.connected</c> event and then carries what is published from now on.
    /// With one, it starts with every buffered event after that id, or, when the buffer no longer
    /// reaches back that far (or the id isn't this hub's), with <c>rockbot.resync</c>.
    /// Returns null for a thread that isn't allowed.
    /// </summary>
    public NotifySubscription? Subscribe(string threadId, string? lastEventId = null) =>
        _threads.TryGetValue(threadId, out var thread) ? thread.Subscribe(lastEventId) : null;
}

/// <summary>One thread's sequence, buffer and subscribers. See <see cref="NotifyHub"/>.</summary>
internal sealed class NotifyThread
{
    private readonly record struct Entry(long Sequence, DateTimeOffset At, string Json);

    private readonly Lock _lock = new();
    private readonly Queue<Entry> _buffer = new();
    private readonly List<NotifySubscription> _subscribers = [];
    private readonly TurnEventMapper _mapper;
    private readonly string _epoch;
    private readonly int _bufferSize;
    private readonly TimeSpan _bufferAge;
    private readonly int _queueSize;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private long _lastSequence;

    public NotifyThread(string threadId, string epoch, AgUiOptions options, TimeProvider clock, ILogger logger)
    {
        ThreadId = threadId;
        _epoch = epoch;
        _bufferSize = Math.Max(0, options.NotifyBufferSize);
        _bufferAge = options.NotifyBufferAge;
        // A reconnect is handed its replay in one go, so the queue has to hold a whole buffer.
        _queueSize = Math.Max(Math.Max(1, options.NotifySubscriberQueueSize), _bufferSize + 1);
        _clock = clock;
        _logger = logger;
        // Not a run, so there is no run id; the mapper only needs one for ids nothing here uses.
        _mapper = new TurnEventMapper(threadId, NotifyEvents.StreamName, clock)
        {
            AlwaysSnapshotState = true,
            AttachmentUrl = AttachmentLinks.Url,
        };
    }

    public string ThreadId { get; }

    public void Publish(Func<TurnEventMapper, IEnumerable<BaseEvent>> map)
    {
        lock (_lock)
        {
            // Mapped under the lock: the mapper keeps the thread's task lists, and events must be
            // numbered in the order they were mapped.
            var events = map(_mapper).ToList();
            if (events.Count == 0)
                return;

            var now = _clock.GetUtcNow();
            foreach (var e in events)
            {
                var entry = new Entry(++_lastSequence, now, AgUiJson.Serialize(e));
                _buffer.Enqueue(entry);
                var frame = new NotifyFrame(Id(entry.Sequence), entry.Json);

                for (var i = _subscribers.Count - 1; i >= 0; i--)
                {
                    if (_subscribers[i].TryEnqueue(frame))
                        continue;

                    _logger.LogWarning(
                        "Dropping a notify subscriber of thread {ThreadId}: it fell {Count} events behind. It can reconnect with Last-Event-ID.",
                        ThreadId, _queueSize);
                    _subscribers[i].Drop();
                    _subscribers.RemoveAt(i);
                }
            }

            Trim(now);
        }
    }

    public NotifySubscription Subscribe(string? lastEventId)
    {
        var subscription = new NotifySubscription(this, _queueSize);
        lock (_lock)
        {
            Trim(_clock.GetUtcNow());

            if (string.IsNullOrEmpty(lastEventId))
            {
                // A new client: tell it where the stream stands, so it has an id to reconnect with
                // even if it loses the connection before anything else is sent.
                subscription.TryEnqueue(Marker(TurnEventMapper.CustomNames.Connected, reason: null));
            }
            else if (!TryParseId(lastEventId, out var last))
            {
                subscription.TryEnqueue(Marker(TurnEventMapper.CustomNames.Resync, NotifyHub.ResyncReasons.Unknown));
            }
            else if (last < OldestReplayable - 1)
            {
                subscription.TryEnqueue(Marker(TurnEventMapper.CustomNames.Resync, NotifyHub.ResyncReasons.Expired));
            }
            else
            {
                foreach (var entry in _buffer)
                {
                    if (entry.Sequence > last)
                        subscription.TryEnqueue(new NotifyFrame(Id(entry.Sequence), entry.Json));
                }
            }

            // Registered under the same lock that numbers events, so nothing published from here
            // on can slip between the replay and the live events, or arrive twice.
            _subscribers.Add(subscription);
        }
        return subscription;
    }

    public void Unsubscribe(NotifySubscription subscription)
    {
        lock (_lock)
            _subscribers.Remove(subscription);
    }

    /// <summary>The lowest sequence a reconnect can still be given: the buffer's first, or the next one when it is empty.</summary>
    private long OldestReplayable => _buffer.Count > 0 ? _buffer.Peek().Sequence : _lastSequence + 1;

    /// <summary>
    /// A stream marker for one subscriber. It is not part of the sequence: its id is the last
    /// event's, so a client that reconnects with it gets everything published after it connected.
    /// </summary>
    private NotifyFrame Marker(string name, string? reason) => new(
        Id(_lastSequence),
        AgUiJson.Serialize(_mapper.Custom(name, new { threadId = ThreadId, reason })));

    private void Trim(DateTimeOffset now)
    {
        while (_buffer.Count > _bufferSize)
            _buffer.Dequeue();

        if (_bufferAge <= TimeSpan.Zero)
            return;
        while (_buffer.Count > 0 && now - _buffer.Peek().At > _bufferAge)
            _buffer.Dequeue();
    }

    private string Id(long sequence) => string.Create(CultureInfo.InvariantCulture, $"{_epoch}-{sequence}");

    /// <summary>True for an id this thread issued: this hub's epoch, and a sequence no later than the last.</summary>
    private bool TryParseId(string id, out long sequence)
    {
        sequence = 0;
        var dash = id.LastIndexOf('-');
        return dash > 0
            && id.AsSpan(0, dash).SequenceEqual(_epoch)
            && long.TryParse(id.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
            && sequence <= _lastSequence;
    }
}

/// <summary>
/// One connection's queue of notify events. The hub writes; the connection's request reads.
/// Dispose it when the connection ends.
/// </summary>
internal sealed class NotifySubscription : IDisposable
{
    private readonly NotifyThread _thread;
    private readonly Channel<NotifyFrame> _queue;
    private readonly CancellationTokenSource _dropped = new();

    internal NotifySubscription(NotifyThread thread, int capacity)
    {
        _thread = thread;
        _queue = Channel.CreateBounded<NotifyFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            // The default mode, Wait: TryWrite fails on a full queue rather than discarding
            // something, which is how the hub learns that this subscriber is too far behind.
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<NotifyFrame> Reader => _queue.Reader;

    /// <summary>Cancelled when the hub drops this subscriber for falling too far behind.</summary>
    public CancellationToken Dropped => _dropped.Token;

    /// <summary>True once the hub has dropped this subscriber.</summary>
    public bool IsDropped => _dropped.IsCancellationRequested;

    /// <summary>False when the queue is full.</summary>
    internal bool TryEnqueue(NotifyFrame frame) => _queue.Writer.TryWrite(frame);

    internal void Drop()
    {
        _queue.Writer.TryComplete();
        // Not Cancel(): that would run the reader's cancellation callbacks here, on the bus's
        // thread and under the thread's lock.
        _ = _dropped.CancelAsync();
    }

    public void Dispose()
    {
        _thread.Unsubscribe(this);
        _queue.Writer.TryComplete();
    }
}
