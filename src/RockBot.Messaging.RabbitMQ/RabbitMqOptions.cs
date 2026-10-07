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

    /// <summary>
    /// Base URL for the RabbitMQ Management HTTP API (e.g., <c>http://localhost:15672</c>).
    /// When null or empty, DLQ depth reporting and DLQ message sampling are disabled.
    /// </summary>
    public string? ManagementApiBaseUrl { get; set; }

    /// <summary>
    /// Retention caps applied as queue arguments to every dead-letter queue.
    /// </summary>
    public RabbitMqDlqRetentionOptions DlqRetention { get; set; } = new();

    /// <summary>
    /// Idle period after which the broker deletes an ephemeral subscription's queues
    /// (<c>x-expires</c>). Default: 24 hours.
    /// </summary>
    public TimeSpan EphemeralQueueExpiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Message TTL on an ephemeral subscription's main queue (<c>x-message-ttl</c>).
    /// A reply nobody consumed within this window is dead-lettered. Default: 1 hour.
    /// </summary>
    public TimeSpan EphemeralMessageTtl { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Dead-letter queue retention caps. Applied as queue arguments when a DLQ is declared,
/// so they hold regardless of broker policies: when a policy also sets a limit, RabbitMQ
/// enforces the stricter of the two. Keep these identical across every process that
/// shares a broker vhost — a DLQ re-declared with different arguments is rejected.
/// </summary>
public sealed class RabbitMqDlqRetentionOptions
{
    /// <summary>Whether to cap DLQs at all. Default: true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Dead-lettered messages expire after this many milliseconds. Default: 72 hours.</summary>
    public long MessageTtlMs { get; set; } = 259_200_000;

    /// <summary>Maximum messages per DLQ; the oldest are dropped beyond it. Default: 10 000.</summary>
    public long MaxLength { get; set; } = 10_000;

    /// <summary>Maximum total body bytes per DLQ; the oldest are dropped beyond it. Default: 256 MiB.</summary>
    public long MaxLengthBytes { get; set; } = 256L * 1024 * 1024;
}
