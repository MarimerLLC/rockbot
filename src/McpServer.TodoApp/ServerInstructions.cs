namespace McpServer.TodoApp;

/// <summary>
/// The instructions returned in the MCP initialize result. Clients that connect directly (without the
/// aggregator's skill document) get the essentials here; SKILL.md has the full guide.
/// </summary>
public static class ServerInstructions
{
    public const string Text =
        "To-do list with recurring tasks. Dates are YYYY-MM-DD and arguments are snake_case; unknown arguments " +
        "and out-of-range enum values are rejected with an error, never ignored. Always read complete_task's " +
        "next and series_ended results to confirm whether a series continued. Record status updates with " +
        "add_task_note, never by editing description. Deletes and completions can be undone with restore_task " +
        "and uncomplete_task. Use list_tasks with compact for an overview and get_task for one task's detail. " +
        "Full guide: the todo-mcp skill document (SKILL.md).";
}
