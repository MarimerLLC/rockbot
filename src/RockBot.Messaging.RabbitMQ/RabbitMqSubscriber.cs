using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of IMessageSubscriber.
/// Creates durable queues bound to the topic exchange for each subscription.
/// </summary>
public sealed class RabbitMqSubscriber : IMessageSubscriber
{
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqSubscriber> _logger;
    private bool _disposed;

    public RabbitMqSubscriber(
        RabbitMqConnectionManager connectionManager,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqSubscriber> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ISubscription> SubscribeAsync(
        string topic,
        string subscriptionName,
        Func<MessageEnvelope, CancellationToken, Task<MessageResult>> handler,
        CancellationToken cancellationToken = default)
    {
        var channel = await _connectionManager.CreateChannelAsync(cancellationToken);

        // Set QoS
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: cancellationToken);

        // Queue name is derived from subscription name for durability
        var queueName = $"rockbot.{subscriptionName}";
        var dlqName = $"{queueName}.dlq";

        // Declare dead-letter queue
        await channel.QueueDeclareAsync(
            queue: dlqName,
            durable: _options.Durable,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: dlqName,
            exchange: _options.DeadLetterExchangeName,
            routingKey: topic,
            cancellationToken: cancellationToken);

        // Declare the main queue with dead-letter routing
        var args = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = _options.DeadLetterExchangeName,
            ["x-dead-letter-routing-key"] = topic
        };

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: _options.Durable,
            exclusive: false,
            autoDelete: false,
            arguments: args,
            cancellationToken: cancellationToken);

        // Bind to the topic exchange
        await channel.QueueBindAsync(
            queue: queueName,
            exchange: _options.ExchangeName,
            routingKey: topic,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Subscribing to topic {Topic} with queue {Queue}",
            topic, queueName);

        // Set up consumer
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (sender, ea) =>
        {
            try
            {
                var envelope = MapToEnvelope(ea);

                _logger.LogDebug(
                    "Received message {MessageId} on topic {Topic}",
                    envelope.MessageId, topic);

                var result = await handler(envelope, cancellationToken);

                switch (result)
                {
                    case MessageResult.Ack:
                        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
                        break;
                    case MessageResult.Retry:
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken);
                        break;
                    case MessageResult.DeadLetter:
                        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message {DeliveryTag}", ea.DeliveryTag);
                await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken);
            }
        };

        var consumerTag = await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        return new RabbitMqSubscription(channel, consumerTag, topic, subscriptionName);
    }

    private static MessageEnvelope MapToEnvelope(BasicDeliverEventArgs ea)
    {
        var props = ea.BasicProperties;
        var headers = new Dictionary<string, string>();

        if (props.Headers is not null)
        {
            foreach (var kvp in props.Headers)
            {
                if (kvp.Key.StartsWith("rb-") && kvp.Value is byte[] bytes)
                {
                    headers[kvp.Key[3..]] = Encoding.UTF8.GetString(bytes);
                }
            }
        }

        // Pull source and destination out of headers
        headers.TryGetValue("source", out var source);
        headers.Remove("source");
        headers.TryGetValue("destination", out var destination);
        headers.Remove("destination");

        return new MessageEnvelope
        {
            MessageId = props.MessageId ?? Guid.NewGuid().ToString("N"),
            MessageType = props.Type ?? "unknown",
            CorrelationId = string.IsNullOrEmpty(props.CorrelationId) ? null : props.CorrelationId,
            ReplyTo = string.IsNullOrEmpty(props.ReplyTo) ? null : props.ReplyTo,
            Source = source ?? "unknown",
            Destination = destination,
            Timestamp = props.Timestamp.UnixTime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(props.Timestamp.UnixTime)
                : DateTimeOffset.UtcNow,
            Body = ea.Body.ToArray(),
            Headers = headers
        };
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
