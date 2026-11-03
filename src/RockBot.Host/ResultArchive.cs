using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Saves the results of agent work to the <see cref="ISavedResponseStore"/> without the model
/// deciding to (#418). Conversation history expires after an idle hour and is wiped by Clear
/// Context, which took finished results with it: the UI showed nothing and the agent had no record
/// of the work, so the user had to ask for it again. A saved copy outlives the session, appears
/// in the UI's saved-responses panel, and can be found again with <c>list_saved_responses</c>.
/// </summary>
/// <remarks>
/// Archiving is best effort: it never throws, so a full disk or a store bug can't cost the user
/// the reply itself. The store applies its own limits on every save, so nothing here needs
/// cleaning up.
/// </remarks>
public sealed class ResultArchive(
    ISavedResponseStore store,
    IOptions<SavedResponseOptions> options,
    ILogger<ResultArchive> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>Longest label kept; labels come from user prompts and task names.</summary>
    internal const int MaxLabelChars = 100;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Saves a background result — subagent synthesis, scheduled task, A2A or MCP task reply —
    /// when it is at least <see cref="SavedResponseOptions.MinAutoSaveChars"/> long.
    /// </summary>
    /// <param name="label">
    /// What the work was: the user's request or the task name. Falls back to the content's first
    /// line when null or blank.
    /// </param>
    /// <returns>The saved entry's id, or null when nothing was saved.</returns>
    public Task<string?> ArchiveAsync(
        string source, string? label, string content, string agentName, string sessionId,
        CancellationToken cancellationToken = default) =>
        content.Trim().Length < options.Value.MinAutoSaveChars
            ? Task.FromResult<string?>(null)
            : SaveAsync(source, label, content, agentName, sessionId, cancellationToken);

    /// <summary>
    /// Saves a direct reply to a user turn, only when the turn used tools and the reply is at least
    /// <see cref="SavedResponseOptions.MinInteractiveAutoSaveChars"/> long — the reply is the
    /// result of work, not conversation.
    /// </summary>
    /// <returns>The saved entry's id, or null when nothing was saved.</returns>
    public Task<string?> ArchiveReplyAsync(
        string userPrompt, string content, int toolCallCount, string agentName, string sessionId,
        CancellationToken cancellationToken = default) =>
        toolCallCount <= 0 || content.Trim().Length < options.Value.MinInteractiveAutoSaveChars
            ? Task.FromResult<string?>(null)
            : SaveAsync(SavedResponseSources.Reply, userPrompt, content, agentName, sessionId, cancellationToken);

    private async Task<string?> SaveAsync(
        string source, string? label, string content, string agentName, string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var saved = new SavedResponse(
                Id: Guid.NewGuid().ToString("N"),
                Label: BuildLabel(label, content),
                Content: content,
                AgentName: agentName,
                SessionId: sessionId,
                SavedAt: _time.GetUtcNow(),
                Source: source,
                AutoSaved: true);

            await store.SaveAsync(saved, cancellationToken);

            logger.LogInformation(
                "Auto-saved {Source} result '{Label}' ({Length} chars) for session {SessionId} as {Id}",
                source, saved.Label, content.Length, sessionId, saved.Id);
            return saved.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not auto-save {Source} result for session {SessionId}", source, sessionId);
            return null;
        }
    }

    /// <summary>
    /// One line of at most <see cref="MaxLabelChars"/>: <paramref name="label"/> when it has text,
    /// otherwise the content's first non-empty line without Markdown heading or emphasis marks.
    /// </summary>
    internal static string BuildLabel(string? label, string content)
    {
        var text = FirstLine(label);
        if (text.Length == 0)
            text = FirstLine(content).TrimStart('#', '*', '_', ' ').TrimEnd('*', '_', ' ');
        if (text.Length == 0)
            text = "Agent result";

        return text.Length <= MaxLabelChars ? text : string.Concat(text.AsSpan(0, MaxLabelChars - 1), "…");
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (trimmed.Length > 0) return trimmed;
        }
        return string.Empty;
    }
}
