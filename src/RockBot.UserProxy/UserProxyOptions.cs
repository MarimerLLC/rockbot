namespace RockBot.UserProxy;

/// <summary>
/// Configuration options for the user proxy service.
/// </summary>
public sealed class UserProxyOptions
{
    public string ProxyId { get; set; } = "user-proxy";

    /// <summary>
    /// When true, this proxy's response queues are ephemeral: deleted when the proxy stops
    /// and expired by the broker after an idle period. Set for short-lived proxies (the CLI)
    /// whose <see cref="ProxyId"/> is unique per run; leave false for long-lived proxies with
    /// a stable identity (Blazor). Default false.
    /// </summary>
    public bool EphemeralQueues { get; set; }

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

    /// <summary>
    /// How long to wait for the agent's reply before giving up. Set from
    /// <c>UserProxy:ReplyTimeout</c> by the Blazor client and <c>--timeout</c> by the CLI.
    /// </summary>
    public TimeSpan DefaultReplyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Once an agent has started a run (<see cref="RunStarted"/>), how long it may stay silent
    /// before the turn is reported as lost. Null (the default) uses three of the heartbeat
    /// intervals the agent advertised.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; set; }

    /// <summary>
    /// The longest a turn may run, heartbeats or not. Heartbeats prove the agent process is alive,
    /// not that the run is getting anywhere, so a run stuck behind other work would otherwise keep
    /// a caller waiting forever. A turn that hits it ends as timed out. Defaults to 30 minutes.
    /// </summary>
    public TimeSpan MaxTurnDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long to wait for the final reply after a <see cref="RunFinished"/> that arrived
    /// first. Events and replies share a queue, so this is normally not waited out.
    /// </summary>
    public TimeSpan RunFinishedGracePeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Base URL of the agent's attachment upload endpoint (e.g.
    /// <c>http://rockbot-agent:8082</c>). When set, file uploads go over HTTP so their bytes
    /// never touch the broker; when unset — or when the endpoint cannot be reached — uploads
    /// fall back to the message bus, which works from anywhere the client already talks to the
    /// agent. Null by default, so a client with no route to the agent's HTTP port keeps working
    /// with no configuration at all.
    /// </summary>
    public string? AttachmentUploadUrl { get; set; }

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
