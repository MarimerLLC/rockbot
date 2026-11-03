namespace RockBot.Host;

/// <summary>
/// An agent response persisted for later reference — saved by the user, by the agent on purpose,
/// or automatically when background work finishes (#418).
/// </summary>
/// <param name="Source">
/// What saved it — one of <see cref="SavedResponseSources"/>. Null on entries written before #418,
/// which were all saved by the user.
/// </param>
/// <param name="AutoSaved">
/// True when the agent host saved it without being asked. Automatic entries are pruned by age,
/// count, and per-label count; explicit ones only by count.
/// </param>
public sealed record SavedResponse(
    string Id,
    string Label,
    string Content,
    string AgentName,
    string SessionId,
    DateTimeOffset SavedAt,
    string? Source = null,
    bool AutoSaved = false);

/// <summary>Values for <see cref="SavedResponse.Source"/>.</summary>
public static class SavedResponseSources
{
    /// <summary>The user saved it (the Blazor save button).</summary>
    public const string User = "user";

    /// <summary>The agent saved it on purpose with <c>save_response</c>.</summary>
    public const string Agent = "agent";

    /// <summary>A consolidated subagent result.</summary>
    public const string Subagent = "subagent";

    /// <summary>A scheduled task's report.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The agent's reply to a result from another agent (A2A).</summary>
    public const string A2A = "a2a";

    /// <summary>The agent's reply to a finished background MCP task.</summary>
    public const string BackgroundTask = "background-task";

    /// <summary>A direct reply to a user turn that did tool work.</summary>
    public const string Reply = "reply";
}
