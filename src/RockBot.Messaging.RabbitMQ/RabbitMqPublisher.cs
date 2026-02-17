using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of IMessagePublisher.
/// Publishes messages to a topic exchange with the topic as the routing key.
/// </summary>
public sealed class RabbitMqPublisher : IMessagePublisher
{
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;
    private IChannel? _channel;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private bool _disposed;

    public RabbitMqPublisher(
        RabbitMqConnectionManager connectionManager,
        Microsoft.Extensions.Options.IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqPublisher> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;
    }

    public async Task PublishAsync(
        string topic,
        MessageEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var channel = await GetChannelAsync(cancellationToken);

        var properties = new BasicProperties
        {
            MessageId = envelope.MessageId,
            Type = envelope.MessageType,
            CorrelationId = envelope.CorrelationId ?? string.Empty,
            ReplyTo = envelope.ReplyTo ?? string.Empty,
            Timestamp = new AmqpTimestamp(envelope.Timestamp.ToUnixTimeSeconds()),
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Headers = new Dictionary<string, object?>()
        };

        // Pack envelope metadata into AMQP headers
        properties.Headers["rb-source"] = envelope.Source;
        if (envelope.Destination is not null)
            properties.Headers["rb-destination"] = envelope.Destination;

        foreach (var header in envelope.Headers)
            properties.Headers[$"rb-{header.Key}"] = header.Value;

        _logger.LogDebug(
            "Publishing message {MessageId} to topic {Topic} (type: {Type})",
            envelope.MessageId, topic, envelope.MessageType);

        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: topic,
            mandatory: false,
            basicProperties: properties,
            body: envelope.Body,
            cancellationToken: cancellationToken);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
            return _channel;

        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
                return _channel;

            _channel = await _connectionManager.CreateChannelAsync(cancellationToken);
            return _channel;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_channel is not null)
            await _channel.CloseAsync();

        _channelLock.Dispose();
    }
}
