using Microsoft.Extensions.Options;
using RockBot.Tools;

namespace RockBot.Host;

/// <summary>
/// Publishes the saved-responses guide through <c>get_tool_guide</c> (#418): what gets saved on
/// its own, how long it is kept, and when to look there before redoing work.
/// </summary>
internal sealed class SavedResponsesSkillProvider(IOptions<SavedResponseOptions> options) : IToolSkillProvider
{
    public string Name => "saved-responses";

    public string Summary =>
        "Finished results kept after the conversation expires: find earlier research and reports " +
        "instead of redoing them, and save content for the user to open later";

    public string GetDocument() =>
        $$"""
        # Saved responses

        Conversation history expires after a period of inactivity and is wiped when the user clears
        context. Saved responses outlive both. The user sees them in the saved-responses panel of the
        web UI; you reach them with these tools.

        ## What is saved without you doing anything

        - The consolidated result of subagent work.
        - A scheduled task's report.
        - Your reply to a result from another agent (A2A) or a finished background MCP task.
        - A long direct reply to the user when the turn used tools.

        Short status lines and ordinary chat are not saved.

        ## Tools

        - `list_saved_responses(query?, max_results?)`: newest first, with id, date, source and label.
          `query` matches words in labels and content.
        - `get_saved_response(id)`: the full content.
        - `save_response(label, content)`: save on purpose. Use it when the user asks you to keep
          something, or for a result you assembled in conversation that they will want again.

        ## Before redoing work

        When the user asks for research, a briefing or a report that sounds like one done earlier,
        or says the result disappeared, call `list_saved_responses` with a few words from the
        request first. If a match exists, give it to them, say when it was produced, and offer to
        refresh it, rather than starting over.

        ## Retention (automatic)

        - Automatic saves are kept for {{options.Value.AutoSavedRetention.TotalDays:0.#}} days, the latest {{options.Value.MaxScheduledRunsPerTask}} runs of each
          scheduled task, and at most {{options.Value.MaxAutoSaved}} in total.
        - Explicit saves (yours or the user's) have no age limit; the oldest go past {{options.Value.MaxExplicitSaved}} entries.
        - Very long content is cut, with a note saying so.

        Nothing needs cleaning up by hand.
        """;
}
