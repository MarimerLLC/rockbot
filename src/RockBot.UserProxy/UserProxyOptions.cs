namespace RockBot.UserProxy;

/// <summary>
/// Configuration options for the user proxy service.
/// </summary>
public sealed class UserProxyOptions
{
    public string ProxyId { get; set; } = "user-proxy";

    /// <summary>
    /// Human-friendly channel name for this proxy ("cli", "blazor", "discord", …), stamped
    /// onto outgoing <see cref="UserMessage.ChannelName"/> so unsolicited replies can show
    /// the user which client originated the work. Defaults to the portion of
    /// <see cref="ProxyId"/> before the first '-' (e.g. "cli-rocky-abc" → "cli").
    /// </summary>
    public string? ChannelName { get; set; }

    /// <summary>Resolves the effective channel name: explicit <see cref="ChannelName"/> if set,
    /// otherwise the prefix of <see cref="ProxyId"/> before the first '-'.</summary>
    public string ResolveChannelName() =>
        !string.IsNullOrWhiteSpace(ChannelName)
            ? ChannelName
            : (ProxyId.Split('-', 2)[0] is { Length: > 0 } prefix ? prefix : ProxyId);

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
