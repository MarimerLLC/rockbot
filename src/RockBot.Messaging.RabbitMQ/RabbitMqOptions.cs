namespace RockBot.Messaging.RabbitMQ;

/// <summary>
/// Configuration options for the RabbitMQ messaging provider.
/// </summary>
public sealed class RabbitMqOptions
{
    /// <summary>
    /// RabbitMQ host name. Default: localhost.
    /// </summary>
    public string HostName { get; set; } = "localhost";

    /// <summary>
    /// RabbitMQ port. Default: 5672.
    /// </summary>
    public int Port { get; set; } = 5672;

    /// <summary>
    /// Username for authentication.
    /// </summary>
    public string UserName { get; set; } = "guest";

    /// <summary>
    /// Password for authentication.
    /// </summary>
    public string Password { get; set; } = "guest";

    /// <summary>
    /// Virtual host. Default: /.
    /// </summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Name of the topic exchange to use. Default: rockbot.
    /// </summary>
    public string ExchangeName { get; set; } = "rockbot";

    /// <summary>
    /// Dead-letter exchange name. Default: rockbot.dlx.
    /// </summary>
    public string DeadLetterExchangeName { get; set; } = "rockbot.dlx";

    /// <summary>
    /// Whether queues and exchanges should be durable. Default: true.
    /// </summary>
    public bool Durable { get; set; } = true;

    /// <summary>
    /// Prefetch count for consumers. Default: 10.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 10;
}
