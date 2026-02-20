using RabbitMQ.Client;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// Represents an active RabbitMQ subscription.
/// Disposing cancels the consumer and closes the channel.
/// </summary>
internal sealed class RabbitMqSubscription : ISubscription
{
    private readonly IChannel _channel;
    private readonly string _consumerTag;
    private bool _disposed;

    public string Topic { get; }
    public string SubscriptionName { get; }
    public bool IsActive => !_disposed && _channel.IsOpen;

    public RabbitMqSubscription(
        IChannel channel,
        string consumerTag,
        string topic,
        string subscriptionName)
    {
        _channel = channel;
        _consumerTag = consumerTag;
        Topic = topic;
        SubscriptionName = subscriptionName;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_channel.IsOpen)
        {
            await _channel.BasicCancelAsync(_consumerTag);
            await _channel.CloseAsync();
        }
    }
}
