using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.UserProxy.WorkIqAuth;

/// <summary>
/// Default <see cref="IWorkIqAuthStatusListener"/> implementation: subscribes
/// to <see cref="WorkIqAuthTopics.Expired"/> on the bus and fans the payload
/// out to UI components via the <see cref="Expired"/> event.
/// </summary>
public sealed class WorkIqAuthStatusListener
    : IWorkIqAuthStatusListener, IHostedService
{
    private readonly IMessageSubscriber _subscriber;
    private readonly ILogger<WorkIqAuthStatusListener> _logger;
    private ISubscription? _subscription;

    public WorkIqAuthStatusListener(
        IMessageSubscriber subscriber,
        ILogger<WorkIqAuthStatusListener> logger)
    {
        _subscriber = subscriber;
        _logger = logger;
    }

    public event EventHandler<WorkIqAuthExpired>? Expired;

    public WorkIqAuthExpired? LastExpired { get; private set; }

    public void ClearLastExpired() => LastExpired = null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = await _subscriber.SubscribeAsync(
            WorkIqAuthTopics.Expired,
            $"ui.workiq.expired.{Guid.NewGuid():N}",
            HandleAsync,
            cancellationToken);
        _logger.LogInformation("WorkIq auth status listener subscribed");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }

    private Task<MessageResult> HandleAsync(MessageEnvelope envelope, CancellationToken ct)
    {
        WorkIqAuthExpired? payload;
        try
        {
            payload = envelope.GetPayload<WorkIqAuthExpired>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize WorkIqAuthExpired payload");
            return Task.FromResult(MessageResult.DeadLetter);
        }
        if (payload is null)
        {
            _logger.LogWarning("Received null WorkIqAuthExpired payload");
            return Task.FromResult(MessageResult.DeadLetter);
        }

        LastExpired = payload;
        try
        {
            Expired?.Invoke(this, payload);
        }
        catch (Exception ex)
        {
            // Subscriber should never crash on a buggy UI handler.
            _logger.LogWarning(ex, "WorkIq auth status subscriber threw");
        }
        return Task.FromResult(MessageResult.Ack);
    }
}
