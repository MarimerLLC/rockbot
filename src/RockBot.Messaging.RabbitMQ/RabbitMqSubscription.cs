using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// Represents an active RabbitMQ subscription.
/// Transparently reconnects the channel and consumer if the channel is closed
/// by the broker or a network event.  Disposing cancels the consumer and
/// closes the channel without triggering a reconnect, and deletes the queues of an
/// ephemeral subscription.
/// </summary>
internal sealed class RabbitMqSubscription : ISubscription, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<(IChannel channel, string consumerTag)>> _channelFactory;
    private readonly ILogger _logger;
    private readonly (string Queue, string? Dlq)? _ephemeralQueues;
    private readonly CancellationTokenSource _disposeCts = new();

    // Updated atomically by the reconnect loop; read in DisposeAsync.
    private volatile IChannel _channel;
    private volatile string _consumerTag;
    private volatile bool _disposed;

    public string Topic { get; }
    public string SubscriptionName { get; }

    /// <summary>
    /// True as long as the subscription has not been explicitly disposed.
    /// Reconnection after a channel failure is transparent — callers do not
    /// need to re-subscribe.
    /// </summary>
    public bool IsActive => !_disposed;

    public RabbitMqSubscription(
        IChannel channel,
        string consumerTag,
        string topic,
        string subscriptionName,
        Func<CancellationToken, Task<(IChannel channel, string consumerTag)>> channelFactory,
        ILogger logger,
        (string Queue, string? Dlq)? ephemeralQueues = null)
    {
        _channel = channel;
        _consumerTag = consumerTag;
        Topic = topic;
        SubscriptionName = subscriptionName;
        _channelFactory = channelFactory;
        _logger = logger;
        _ephemeralQueues = ephemeralQueues;

        RegisterShutdownHandler(channel);
    }

    private void RegisterShutdownHandler(IChannel channel)
    {
        channel.ChannelShutdownAsync += OnChannelShutdownAsync;
    }

    private Task OnChannelShutdownAsync(object sender, ShutdownEventArgs args)
    {
        // Application-initiated close is the dispose path — no reconnect needed.
        if (_disposed || args.Initiator == ShutdownInitiator.Application)
            return Task.CompletedTask;

        _logger.LogWarning(
            "Subscription channel for {Topic} ({SubscriptionName}) closed unexpectedly " +
            "({Initiator} {ReplyCode}: {ReplyText}) — reconnecting",
            Topic, SubscriptionName, args.Initiator, args.ReplyCode, args.ReplyText);

        _ = ReconnectAsync();
        return Task.CompletedTask;
    }

    private async Task ReconnectAsync()
    {
        var delay = TimeSpan.FromSeconds(2);
        const double backoffMultiplier = 2.0;
        const int maxDelaySeconds = 30;

        while (!_disposed)
        {
            try
            {
                await Task.Delay(delay, _disposeCts.Token);
            }
            catch (OperationCanceledException)
            {
                return; // disposed while waiting
            }

            try
            {
                var (newChannel, newConsumerTag) = await _channelFactory(_disposeCts.Token);
                RegisterShutdownHandler(newChannel);

                _channel = newChannel;
                _consumerTag = newConsumerTag;

                _logger.LogInformation(
                    "Reconnected subscription for {Topic} ({SubscriptionName})",
                    Topic, SubscriptionName);
                return;
            }
            catch (OperationCanceledException) when (_disposed)
            {
                return; // disposed during reconnect attempt
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconnect attempt for {Topic} ({SubscriptionName}) failed — retrying in {Delay}s",
                    Topic, SubscriptionName, delay.TotalSeconds);

                delay = TimeSpan.FromSeconds(
                    Math.Min(delay.TotalSeconds * backoffMultiplier, maxDelaySeconds));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _disposeCts.CancelAsync();
        _disposeCts.Dispose();

        var channel = _channel;
        if (channel.IsOpen)
        {
            try { await channel.BasicCancelAsync(_consumerTag); } catch { /* best-effort */ }

            // An ephemeral subscription's queues die with it. The DLQ is only removed when
            // empty so dead-letters stay inspectable until the broker's x-expires reaps it.
            // A process that crashes skips this; x-expires covers that case too.
            if (_ephemeralQueues is (string queue, var dlq))
            {
                try
                {
                    await channel.QueueDeleteAsync(queue);
                    if (dlq is not null)
                        await channel.QueueDeleteAsync(dlq, ifUnused: false, ifEmpty: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Best-effort delete of ephemeral queue {Queue} failed", queue);
                }
            }

            if (channel.IsOpen)
                await channel.CloseAsync();
        }
    }
}
