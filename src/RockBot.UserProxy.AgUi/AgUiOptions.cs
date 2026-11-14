namespace RockBot.UserProxy.AgUi;

/// <summary>Settings for the AG-UI endpoints, bound from the <c>AgUi</c> configuration section.</summary>
public sealed class AgUiOptions
{
    public const string SectionName = "AgUi";

    /// <summary>The thread id clients may use when <see cref="AllowedThreadIds"/> is empty.</summary>
    public const string DefaultThreadId = "agui-session";

    /// <summary>
    /// The thread ids (RockBot session ids) clients may use; any other gets HTTP 403. Each
    /// session has its own history and working memory on the agent, so the list is short on
    /// purpose. Empty (the default) allows only <see cref="DefaultThreadId"/>.
    /// </summary>
    /// <remarks>
    /// Left empty rather than pre-filled, because configuration binding adds to a list instead
    /// of replacing it: a pre-filled default could never be removed.
    /// </remarks>
    public List<string> AllowedThreadIds { get; set; } = [];

    /// <summary>The <see cref="UserMessage.UserId"/> for every caller while there is no app auth.</summary>
    public string DefaultUserId { get; set; } = "agui-user";

    /// <summary>
    /// How often an idle run or notify stream gets an SSE comment, so proxies and mobile networks
    /// don't drop it while nothing is happening.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many notify events the proxy keeps for each thread, so a client that reconnects with
    /// <c>Last-Event-ID</c> gets what it missed. Zero keeps none: every reconnect that missed
    /// something is told to resync.
    /// </summary>
    public int NotifyBufferSize { get; set; } = 200;

    /// <summary>How long a notify event is kept for replay. Zero or less keeps events until <see cref="NotifyBufferSize"/> pushes them out.</summary>
    public TimeSpan NotifyBufferAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How many notify events may wait for one connected client. A client that falls further
    /// behind is disconnected, and gets what it missed when it reconnects. Never less than a
    /// whole buffer, which a reconnecting client is handed at once.
    /// </summary>
    public int NotifySubscriberQueueSize { get; set; } = 256;

    /// <summary>
    /// How long a run that resumed an MCP question waits for the agent's reply, once the agent
    /// has accepted the answer. After that the run ends with <c>rockbot.reply_pending</c> and the
    /// reply, if one comes, arrives on the notify stream. (Some answers get no reply: the one
    /// that goes on to a background task, for example.)
    /// </summary>
    public TimeSpan ResumeReplyTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>True when clients may use <paramref name="threadId"/>. The match is exact.</summary>
    public bool IsThreadAllowed(string? threadId) =>
        !string.IsNullOrEmpty(threadId) && ThreadIds().Contains(threadId, StringComparer.Ordinal);

    /// <summary>The thread ids clients may use: the configured ones, or <see cref="DefaultThreadId"/> when none are.</summary>
    public IReadOnlyList<string> ThreadIds()
    {
        var configured = AllowedThreadIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        return configured.Count == 0 ? [DefaultThreadId] : configured;
    }
}
