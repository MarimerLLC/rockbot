namespace RockBot.UserProxy;

/// <summary>
/// Configuration options for the user proxy service.
/// </summary>
public sealed class UserProxyOptions
{
    public string ProxyId { get; set; } = "user-proxy";
    public TimeSpan DefaultReplyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Maximum number of retry attempts for the initial <c>user.response</c> subscription
    /// when the message bus is unavailable at startup. Use <c>0</c> for no retries or
    /// <see cref="int.MaxValue"/> for unlimited retries.
    /// </summary>
    public int MaxSubscribeRetries { get; set; } = int.MaxValue;

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
