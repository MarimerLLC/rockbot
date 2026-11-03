using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// LLM-callable access to the saved-response store (#418): finished results the agent host saved
/// automatically, entries the user saved from the UI, and entries the agent saved on purpose.
/// The main use is not redoing work: when conversation history has expired, the result of earlier
/// research or a report is still here.
/// </summary>
/// <remarks>Built per turn, so saves are attributed to the session and agent that made them.</remarks>
public sealed class SavedResponseTools
{
    /// <summary>Most entries one list call returns.</summary>
    internal const int MaxListResults = 50;

    private readonly ISavedResponseStore _store;
    private readonly string _sessionId;
    private readonly string _agentName;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public SavedResponseTools(
        ISavedResponseStore store, string sessionId, string agentName, ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _sessionId = sessionId;
        _agentName = agentName;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;

        Tools =
        [
            AIFunctionFactory.Create(ListSavedResponses,
                new AIFunctionFactoryOptions { Name = "list_saved_responses" }),
            AIFunctionFactory.Create(GetSavedResponse,
                new AIFunctionFactoryOptions { Name = "get_saved_response" }),
            AIFunctionFactory.Create(SaveResponse,
                new AIFunctionFactoryOptions { Name = "save_response" }),
        ];
    }

    /// <summary>The <see cref="AIFunction"/> instances for <see cref="ChatOptions.Tools"/>.</summary>
    public IList<AITool> Tools { get; }

    [Description("List saved results, newest first. Results of earlier work — subagent research, " +
                 "scheduled-task reports, replies from other agents, long answers that used tools — are " +
                 "saved automatically, and kept after the conversation history expires. Before redoing " +
                 "research or a report the user asked for earlier, check here: if a matching result exists, " +
                 "offer it (with its date) instead of starting over. Each line gives id, date, source and label.")]
    public async Task<string> ListSavedResponses(
        [Description("Optional words to match against labels and content, case-insensitive. Every word must match.")]
        string? query = null,
        [Description("How many entries to return (1-50). Default 15.")]
        int max_results = 15)
    {
        _logger.LogInformation("Tool call: list_saved_responses(query={Query}, max={Max})", query, max_results);

        var take = Math.Clamp(max_results, 1, MaxListResults);
        var words = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var all = await _store.ListAsync();
        var matches = all
            .Where(r => words.All(w =>
                r.Label.Contains(w, StringComparison.OrdinalIgnoreCase)
                || r.Content.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(take)
            .ToList();

        if (matches.Count == 0)
            return words.Length == 0
                ? "No saved responses."
                : $"No saved responses match \"{query}\".";

        var sb = new StringBuilder();
        sb.AppendLine($"{matches.Count} saved response(s), newest first:");
        foreach (var r in matches)
        {
            var source = r.Source ?? SavedResponseSources.User;
            sb.AppendLine($"- id={r.Id} | {r.SavedAt:yyyy-MM-dd HH:mm} UTC | {source} | {r.Label} ({r.Content.Length:N0} chars)");
        }
        sb.Append("Use get_saved_response(id) to read one.");
        return sb.ToString();
    }

    [Description("Read the full content of a saved response by id (from list_saved_responses).")]
    public async Task<string> GetSavedResponse(
        [Description("The id from list_saved_responses")] string id)
    {
        _logger.LogInformation("Tool call: get_saved_response({Id})", id);

        var r = string.IsNullOrWhiteSpace(id) ? null : await _store.GetAsync(id.Trim());
        if (r is null)
            return $"No saved response with id '{id}'. Call list_saved_responses to see what exists.";

        return $"Saved response '{r.Label}' — saved {r.SavedAt:yyyy-MM-dd HH:mm} UTC " +
               $"({r.Source ?? SavedResponseSources.User}, agent {r.AgentName}):\n\n{r.Content}";
    }

    [Description("Save content so the user can open it later from the saved-responses panel, and so it " +
                 "survives after this conversation expires. Results of background work are already saved " +
                 "automatically — use this when the user asks you to keep something, or for a result you " +
                 "built in this conversation that they will want again. Explicit saves are kept until the " +
                 "store's size limit, not pruned by age.")]
    public async Task<string> SaveResponse(
        [Description("Short label the user will recognise in a list, e.g. 'Q3 vendor comparison'")] string label,
        [Description("The full content to save, as Markdown")] string content)
    {
        _logger.LogInformation("Tool call: save_response(label={Label}, {Length} chars)", label, content?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(content))
            return "Nothing saved: content is empty.";

        var saved = new SavedResponse(
            Id: Guid.NewGuid().ToString("N"),
            Label: ResultArchive.BuildLabel(label, content),
            Content: content,
            AgentName: _agentName,
            SessionId: _sessionId,
            SavedAt: _time.GetUtcNow(),
            Source: SavedResponseSources.Agent,
            AutoSaved: false);

        await _store.SaveAsync(saved);
        return $"Saved as '{saved.Label}' (id={saved.Id}). The user can open it from the saved-responses panel.";
    }
}
