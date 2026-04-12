namespace RockBot.UserProxy;

/// <summary>
/// Configuration options for the user proxy service.
/// </summary>
public sealed class UserProxyOptions
{
    public string ProxyId { get; set; } = "user-proxy";

    /// <summary>
    /// Name of the agent this proxy communicates with.
    /// Used to scope message bus topics so multiple agent instances can share the same broker.
    /// </summary>
    public string AgentName { get; set; } = "RockBot";
    public TimeSpan DefaultReplyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Maximum number of retry attempts for the initial <c>user.response</c> subscription
    /// when the message bus is unavailable at startup. Use <c>0</c> to disable retries.
    /// With the default base delay of 2 s and 30 s cap, 15 attempts covers roughly 5 minutes.
    /// </summary>
    public int MaxSubscribeRetries { get; set; } = 15;

    /// <summary>
    /// Base delay between subscription retry attempts. Each subsequent retry doubles
    /// this value (exponential backoff), capped at <see cref="MaxSubscribeRetryDelay"/>.
    /// </summary>
    public TimeSpan SubscribeRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound for the exponential backoff delay between subscription retries.
    /// </summary>
    public TimeSpan MaxSubscribeRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
