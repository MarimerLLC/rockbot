using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// RabbitMQ implementation of IMessageSubscriber.
/// Creates durable queues bound to the topic exchange for each subscription.
/// Subscriptions self-heal: if the underlying channel is closed unexpectedly,
/// <see cref="RabbitMqSubscription"/> reconnects transparently.
/// </summary>
public sealed class RabbitMqSubscriber : IMessageSubscriber
{
    private readonly RabbitMqConnectionManager _connectionManager;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqSubscriber> _logger;

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
        var queueName = $"rockbot.{subscriptionName}";
        var dlqName = $"{queueName}.dlq";
        var exchangeName = _options.ExchangeName;
        var dlxName = _options.DeadLetterExchangeName;
        var prefetchCount = _options.PrefetchCount;
        var durable = _options.Durable;

        // Factory that creates a fresh channel + consumer, called both for initial
        // setup and for transparent reconnection after unexpected channel closure.
        async Task<(IChannel channel, string consumerTag)> CreateChannelAndConsumerAsync(
            CancellationToken ct)
        {
            var channel = await _connectionManager.CreateChannelAsync(ct);

            await channel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: prefetchCount,
                global: false,
                cancellationToken: ct);

            // Declare dead-letter queue
            await channel.QueueDeclareAsync(
                queue: dlqName,
                durable: durable,
                exclusive: false,
                autoDelete: false,
                cancellationToken: ct);

            await channel.QueueBindAsync(
                queue: dlqName,
                exchange: dlxName,
                routingKey: topic,
                cancellationToken: ct);

            // Declare the main queue with dead-letter routing
            var args = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = dlxName,
                ["x-dead-letter-routing-key"] = topic
            };

            await channel.QueueDeclareAsync(
                queue: queueName,
                durable: durable,
                exclusive: false,
                autoDelete: false,
                arguments: args,
                cancellationToken: ct);

            await channel.QueueBindAsync(
                queue: queueName,
                exchange: exchangeName,
                routingKey: topic,
                cancellationToken: ct);

            // Set up consumer — each factory call produces its own handler closure
            // that captures the new channel, so ack/nack always targets the right channel.
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, ea) =>
            {
                var envelope = MapToEnvelope(ea);

                var parentContext = TraceContextPropagator.Extract(envelope.Headers);

                using var activity = RabbitMqDiagnostics.Source.StartActivity(
                    "process", ActivityKind.Consumer,
                    parentContext ?? default);

                if (activity is not null)
                {
                    activity.SetTag("messaging.system", "rabbitmq");
                    activity.SetTag("messaging.destination", topic);
                    activity.SetTag("messaging.message_id", envelope.MessageId);
                }

                RabbitMqDiagnostics.ActiveMessages.Add(1);
                var sw = Stopwatch.StartNew();
                MessageResult result;

                try
                {
                    _logger.LogDebug(
                        "Received message {MessageId} on topic {Topic}",
                        envelope.MessageId, topic);

                    result = await handler(envelope, CancellationToken.None);

                    sw.Stop();
                    activity?.SetTag("messaging.result", result.ToString().ToLowerInvariant());

                    switch (result)
                    {
                        case MessageResult.Ack:
                            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false);
                            break;
                        case MessageResult.Retry:
                            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                            break;
                        case MessageResult.DeadLetter:
                            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    result = MessageResult.Retry;
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity?.SetTag("messaging.result", "error");
                    _logger.LogError(ex, "Error processing message {DeliveryTag}", ea.DeliveryTag);
                    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true);
                }
                finally
                {
                    RabbitMqDiagnostics.ActiveMessages.Add(-1);
                    RabbitMqDiagnostics.ProcessDuration.Record(sw.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("messaging.destination", topic),
                        new KeyValuePair<string, object?>("messaging.result",
                            activity?.GetTagItem("messaging.result") ?? "unknown"));
                    RabbitMqDiagnostics.ProcessMessages.Add(1,
                        new KeyValuePair<string, object?>("messaging.destination", topic),
                        new KeyValuePair<string, object?>("messaging.result",
                            activity?.GetTagItem("messaging.result") ?? "unknown"));
                }
            };

            var consumerTag = await channel.BasicConsumeAsync(
                queue: queueName,
                autoAck: false,
                consumer: consumer,
                cancellationToken: ct);

            return (channel, consumerTag);
        }

        _logger.LogInformation(
            "Subscribing to topic {Topic} with queue {Queue}",
            topic, queueName);

        var (channel, consumerTag) = await CreateChannelAndConsumerAsync(cancellationToken);

        return new RabbitMqSubscription(
            channel,
            consumerTag,
            topic,
            subscriptionName,
            CreateChannelAndConsumerAsync,
            _logger);
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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
