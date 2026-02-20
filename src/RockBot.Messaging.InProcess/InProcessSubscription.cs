using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RockBot.Messaging;

namespace RockBot.Messaging.InProcess;

internal sealed class InProcessSubscription : ISubscription
{
    private const int MaxRetries = 3;

    private readonly Channel<(string Topic, MessageEnvelope Envelope, int RetryCount)> _channel;
    private readonly Func<MessageEnvelope, CancellationToken, Task<MessageResult>> _handler;
    private readonly InProcessBus _bus;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private int _disposed;

    public string Topic { get; }
    public string SubscriptionName { get; }
    public bool IsActive => _disposed == 0;

    public InProcessSubscription(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        InProcessBus bus,
        ILogger logger)
    {
        Topic = topic;
        SubscriptionName = subscriptionName;
        _handler = handler;
        _bus = bus;
        _logger = logger;
        _channel = Channel.CreateUnbounded<(string, MessageEnvelope, int)>();
        _pump = Task.Run(PumpAsync);
    }

    internal async ValueTask EnqueueAsync(string topic, MessageEnvelope envelope, CancellationToken ct)
    {
        if (_disposed != 0) return;
        await _channel.Writer.WriteAsync((topic, envelope, 0), ct);
    }

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        try
        {
            await foreach (var (topic, envelope, retryCount) in _channel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var result = await _handler(envelope, ct);
                    switch (result)
                    {
                        case MessageResult.Ack:
                            break;
                        case MessageResult.Retry:
                            if (retryCount < MaxRetries)
                            {
                                _logger.LogWarning(
                                    "Message {MessageId} on {Topic} returned Retry (attempt {Attempt}/{Max}), re-enqueueing",
                                    envelope.MessageId, topic, retryCount + 1, MaxRetries);
                                await _channel.Writer.WriteAsync((topic, envelope, retryCount + 1), CancellationToken.None);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "Message {MessageId} on {Topic} exhausted retries ({Max}), discarding",
                                    envelope.MessageId, topic, MaxRetries);
                            }
                            break;
                        case MessageResult.DeadLetter:
                            _logger.LogError(
                                "Message {MessageId} on {Topic} dead-lettered, discarding",
                                envelope.MessageId, topic);
                            break;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception processing message {MessageId} on {Topic}",
                        envelope.MessageId, topic);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _bus.Unregister(this);
        _cts.Cancel();
        _channel.Writer.TryComplete();

        try { await _pump; }
        catch (OperationCanceledException) { }

        _cts.Dispose();
    }
}
