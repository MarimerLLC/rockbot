namespace RockBot.Tools.Web;

/// <summary>
/// Serializes calls to a search provider and spaces out the request starts, so a burst of
/// parallel <c>web_search</c> calls (common when a subagent fans out several queries in one
/// tool-call batch) does not overrun the provider's per-second quota.
/// </summary>
/// <remarks>
/// <para>
/// A provider owns one throttle and the provider is a DI singleton, so the throttle is shared
/// by every session and subagent in the process. That matches what is being protected: the
/// quota belongs to the provider's API key, not to any one conversation.
/// </para>
/// <para>
/// A caller holds a <see cref="Lease"/> for the whole search, including its retries, and calls
/// <see cref="Lease.WaitForSlotAsync"/> before each HTTP attempt. A retry can push the next
/// slot further out with <see cref="Lease.DeferNextSlot"/>, which also holds back whoever runs
/// next, so a provider that has just said "slow down" is not hit again straight away.
/// </para>
/// </remarks>
internal sealed class WebSearchThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minInterval;
    private readonly TimeProvider _time;

    // Both are only read or written while _gate is held.
    private long? _lastStartTimestamp;
    private long? _notBeforeTimestamp;

    public WebSearchThrottle(TimeSpan minInterval, TimeProvider? timeProvider = null)
    {
        _minInterval = minInterval < TimeSpan.Zero ? TimeSpan.Zero : minInterval;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The minimum spacing enforced between request starts.</summary>
    public TimeSpan MinInterval => _minInterval;

    /// <summary>
    /// Waits for exclusive use of the provider. Dispose the lease to let the next caller in.
    /// </summary>
    public async Task<Lease> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(this);
    }

    private async Task WaitForSlotAsync(CancellationToken ct)
    {
        var wait = TimeSpan.Zero;

        if (_lastStartTimestamp is { } lastStart)
        {
            var sinceLast = _time.GetElapsedTime(lastStart);
            if (_minInterval - sinceLast > wait)
                wait = _minInterval - sinceLast;
        }

        if (_notBeforeTimestamp is { } notBefore)
        {
            // GetElapsedTime(start) is negative while "start" is still in the future.
            var untilNotBefore = -_time.GetElapsedTime(notBefore);
            if (untilNotBefore > wait)
                wait = untilNotBefore;
        }

        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);

        _notBeforeTimestamp = null;
        _lastStartTimestamp = _time.GetTimestamp();
    }

    private void DeferNextSlot(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return;

        var target = _time.GetTimestamp() + (long)(delay.TotalSeconds * _time.TimestampFrequency);
        if (_notBeforeTimestamp is not { } existing || target > existing)
            _notBeforeTimestamp = target;
    }

    /// <summary>
    /// Exclusive hold on the provider. Not thread-safe; one caller uses it, then disposes it.
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private WebSearchThrottle? _owner;

        internal Lease(WebSearchThrottle owner) => _owner = owner;

        /// <summary>
        /// Waits until the next request may start (the minimum spacing since the previous
        /// start, and any deferral from a rate-limit response), then records this start.
        /// </summary>
        public Task WaitForSlotAsync(CancellationToken ct) =>
            (_owner ?? throw new ObjectDisposedException(nameof(Lease))).WaitForSlotAsync(ct);

        /// <summary>
        /// Holds the next request start back by at least <paramref name="delay"/> from now.
        /// </summary>
        public void DeferNextSlot(TimeSpan delay) =>
            (_owner ?? throw new ObjectDisposedException(nameof(Lease))).DeferNextSlot(delay);

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?._gate.Release();
        }
    }
}
