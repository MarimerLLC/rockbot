namespace RockBot.Host;

/// <summary>
/// Thread-safe implementation of <see cref="IUserActivityMonitor"/>.
/// Stores the timestamp of the most recent user message using a <c>long</c>
/// (UTC ticks) so reads and writes are atomic without a lock.
/// </summary>
internal sealed class UserActivityMonitor : IUserActivityMonitor
{
    private long _lastActivityTicks = 0;

    /// <inheritdoc/>
    public void RecordActivity() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTimeOffset.UtcNow.Ticks);

    /// <inheritdoc/>
    public bool IsUserActive(TimeSpan idleThreshold)
    {
        var ticks = Interlocked.Read(ref _lastActivityTicks);
        if (ticks == 0) return false;
        return DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero) < idleThreshold;
    }
}
