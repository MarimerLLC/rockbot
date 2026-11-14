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
    /// How often an idle run stream gets an SSE comment, so proxies and mobile networks don't
    /// drop it while the agent works.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>True when clients may use <paramref name="threadId"/>. The match is exact.</summary>
    public bool IsThreadAllowed(string? threadId)
    {
        if (string.IsNullOrEmpty(threadId))
            return false;

        var configured = AllowedThreadIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        return configured.Count == 0
            ? string.Equals(threadId, DefaultThreadId, StringComparison.Ordinal)
            : configured.Contains(threadId, StringComparer.Ordinal);
    }
}
