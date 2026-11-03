namespace RockBot.Host;

/// <summary>
/// Configuration for the saved-response store. Every limit is enforced by the store itself on
/// load and on each save, so the store stays bounded without any manual cleanup (#418).
/// </summary>
public sealed class SavedResponseOptions
{
    public string BasePath { get; set; } = "saved-responses";

    /// <summary>
    /// Longest content kept for one entry. Longer content is cut, with a note saying so.
    /// Bounds the size of any single file, whoever saved it.
    /// </summary>
    public int MaxContentChars { get; set; } = 100_000;

    /// <summary>
    /// Most automatically saved results kept (<see cref="SavedResponse.AutoSaved"/>). The oldest
    /// are pruned first.
    /// </summary>
    public int MaxAutoSaved { get; set; } = 200;

    /// <summary>
    /// Automatically saved results older than this are pruned. A result worth keeping longer
    /// can be saved explicitly, which is not subject to this window.
    /// </summary>
    public TimeSpan AutoSavedRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Most scheduled-task reports kept per task (<see cref="SavedResponseSources.Scheduled"/>
    /// entries share a label: the task name). A daily briefing or hourly patrol would otherwise fill
    /// the store with copies of the same report and push out one-off results. The newest are kept.
    /// </summary>
    public int MaxScheduledRunsPerTask { get; set; } = 3;

    /// <summary>
    /// Most explicitly saved entries kept (the Blazor save button, or the agent's
    /// <c>save_response</c> tool). They have no age limit; past this count the oldest are pruned.
    /// </summary>
    public int MaxExplicitSaved { get; set; } = 500;

    /// <summary>
    /// Shortest background result (subagent, scheduled task, A2A, MCP task) saved automatically.
    /// Shorter output is a status line, not a result worth finding later.
    /// </summary>
    public int MinAutoSaveChars { get; set; } = 200;

    /// <summary>
    /// Shortest direct reply to a user turn saved automatically. Such a reply is saved only when
    /// the turn also used tools, so ordinary chat stays out of the store.
    /// </summary>
    public int MinInteractiveAutoSaveChars { get; set; } = 1_000;
}
