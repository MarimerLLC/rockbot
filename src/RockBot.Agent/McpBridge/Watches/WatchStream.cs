namespace RockBot.Agent.McpBridge.Watches;

/// <summary>
/// One active watch, in memory: its ledger entry and the state of its pending read.
/// </summary>
internal sealed class ActiveWatch(McpWatchEntry entry, bool confirmed)
{
    /// <summary>The bridge's own copy of the watch's ledger entry.</summary>
    public McpWatchEntry Entry { get; } = entry;

    public string WatchId => Entry.WatchId;

    /// <summary>
    /// The server accepted the subscription (or, for a watch resumed after a restart, did before).
    /// Until then a stream's acknowledgement is the watch request's to judge, not the catch-up's.
    /// </summary>
    public volatile bool Confirmed = confirmed;

    /// <summary>1 while a read is scheduled and hasn't started; further updates fold into it.</summary>
    public int ReadScheduled;

    /// <summary>Consecutive failed reads.</summary>
    public int ReadFailures;

    /// <summary>Serializes the watch's reads, so two never deliver the same change twice.</summary>
    public SemaphoreSlim ReadGate { get; } = new(1, 1);
}

/// <summary>What one opening of a server's listen stream asked for and was granted.</summary>
/// <param name="Requested">The URIs the stream asked for.</param>
/// <param name="Granted">The URIs the server acknowledged.</param>
/// <param name="Failure">Why the stream didn't open, or null when it did.</param>
/// <param name="Final">The stream was stopped for good: no further opening will come.</param>
internal sealed record WatchStreamOpening(
    IReadOnlySet<string> Requested, IReadOnlySet<string> Granted, string? Failure, bool Final = false);

/// <summary>
/// The bridge's <c>subscriptions/listen</c> stream to one server, carrying every URI watched there.
/// The loop that runs it lives in <see cref="McpBridgeService"/>; this holds its shared state.
/// </summary>
internal sealed class WatchStream(string server, CancellationToken lifetime)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private TaskCompletionSource _wake = NewSignal();
    private TaskCompletionSource<WatchStreamOpening> _nextOpening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<IReadOnlyList<string>>? _pendingAck;
    private string? _subscriptionId;
    private IReadOnlySet<string> _granted = new HashSet<string>(StringComparer.Ordinal);
    private int _version;

    /// <summary>The server's name, as the bridge's connections know it.</summary>
    public string Server { get; } = server;

    /// <summary>Cancelled when the stream is stopped for good, or the bridge stops.</summary>
    public CancellationTokenSource LoopCts { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

    /// <summary>The loop running the stream.</summary>
    public Task? Loop { get; set; }

    /// <summary>Stopped for good: nothing on this server is watched any more.</summary>
    public bool Stopped { get; private set; }

    /// <summary>The URIs the open stream's server acknowledged.</summary>
    public IReadOnlySet<string> Granted
    {
        get { lock (_gate) return _granted; }
    }

    /// <summary>
    /// Moves on every <see cref="Recycle"/>. The loop reads it before it reads what to subscribe to,
    /// so a recycle that lands before the stream opens is never lost.
    /// </summary>
    public int Version
    {
        get { lock (_gate) return _version; }
    }

    /// <summary>Completes when the loop is asked to reopen now (or stop).</summary>
    public Task WakeTask
    {
        get { lock (_gate) return _wake.Task; }
    }

    /// <summary>Completes with the next opening of the stream, successful or not.</summary>
    public Task<WatchStreamOpening> NextOpening
    {
        get { lock (_gate) return _nextOpening.Task; }
    }

    /// <summary>Closes the open stream, if any, so the loop opens it again at once with the current URIs.</summary>
    public void Recycle()
    {
        TaskCompletionSource wake;
        lock (_gate)
        {
            try
            {
                _current?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already closing.
            }

            _version++;
            wake = _wake;
            _wake = NewSignal();
        }

        wake.TrySetResult();
    }

    /// <summary>Stops the stream for good.</summary>
    public void Stop()
    {
        lock (_gate) Stopped = true;
        Opened(new WatchStreamOpening(new HashSet<string>(), new HashSet<string>(),
            "the server's watches were ended", Final: true));
        Recycle();
        try
        {
            LoopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already stopped.
        }
    }

    /// <summary>
    /// Records the stream being opened, so <see cref="Recycle"/> can close it and its acknowledgement
    /// find it. When a recycle came after <paramref name="version"/> was read, the opening is
    /// cancelled at once: what it was about to subscribe to is already out of date.
    /// </summary>
    public TaskCompletionSource<IReadOnlyList<string>> BeginOpening(CancellationTokenSource current, string subscriptionId, int version)
    {
        var ack = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool stale;
        lock (_gate)
        {
            _current = current;
            _subscriptionId = subscriptionId;
            _pendingAck = ack;
            _granted = new HashSet<string>(StringComparer.Ordinal);
            stale = _version != version;
        }

        if (stale)
            current.Cancel();

        return ack;
    }

    /// <summary>
    /// Takes an acknowledgement the server sent. A tagged one must carry the open stream's id; an
    /// untagged one is taken for the stream being opened.
    /// </summary>
    public bool Acknowledge(string? subscriptionId, IReadOnlyList<string> granted)
    {
        TaskCompletionSource<IReadOnlyList<string>>? ack;
        lock (_gate)
        {
            if (subscriptionId is not null && !string.Equals(subscriptionId, _subscriptionId, StringComparison.Ordinal))
                return false;
            ack = _pendingAck;
        }

        return ack?.TrySetResult(granted) ?? false;
    }

    /// <summary>Whether a tagged notification belongs to the open stream (untagged ones are taken as its).</summary>
    public bool IsCurrent(string? subscriptionId)
    {
        if (subscriptionId is null)
            return true;
        lock (_gate) return string.Equals(subscriptionId, _subscriptionId, StringComparison.Ordinal);
    }

    /// <summary>Records how an opening went and tells whoever waits for it.</summary>
    public void Opened(WatchStreamOpening opening)
    {
        TaskCompletionSource<WatchStreamOpening> next;
        lock (_gate)
        {
            if (opening.Failure is null)
                _granted = opening.Granted;
            next = _nextOpening;
            _nextOpening = new TaskCompletionSource<WatchStreamOpening>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        next.TrySetResult(opening);
    }

    /// <summary>Forgets the stream that just closed.</summary>
    public void EndOpening(CancellationTokenSource current)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, current))
                return;
            _current = null;
            _subscriptionId = null;
            _pendingAck = null;
            _granted = new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
