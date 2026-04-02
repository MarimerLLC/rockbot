using System.Collections.Concurrent;

namespace RockBot.Host;

/// <summary>
/// Thread-safe in-memory queue for inbound A2A notifications.
/// </summary>
internal sealed class InboundNotificationQueue : IInboundNotificationQueue
{
    private readonly ConcurrentQueue<InboundNotification> _queue = new();

    public int PendingCount => _queue.Count;

    public Task EnqueueAsync(InboundNotification notification, CancellationToken ct)
    {
        _queue.Enqueue(notification);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InboundNotification>> DrainAsync(CancellationToken ct)
    {
        var items = new List<InboundNotification>();
        while (_queue.TryDequeue(out var item))
            items.Add(item);
        return Task.FromResult<IReadOnlyList<InboundNotification>>(items);
    }
}
