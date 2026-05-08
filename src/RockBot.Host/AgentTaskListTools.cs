using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// LLM-callable tools for the per-run task list (<see cref="AgentTaskList"/>). Built
/// once per <see cref="AgentLoopRunner.RunAsync"/> invocation over a fresh state object.
///
/// The list itself is rendered into a system message that the loop refreshes from the
/// underlying state — so even if old <c>task_create</c>/<c>task_update</c> tool results
/// get trimmed by <c>TrimLargeToolResults</c>, the model still sees the current list at
/// the next iteration.
/// </summary>
internal sealed class AgentTaskListTools
{
    private readonly AgentTaskList _taskList;
    private readonly ILogger _logger;

    public AgentTaskListTools(AgentTaskList taskList, ILogger logger)
    {
        _taskList = taskList;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(TaskCreate),
            AIFunctionFactory.Create(TaskUpdate)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description(
        "Record a multi-step plan for the current request as a structured task list. " +
        "Use this once at the start of any non-trivial work to lay out the steps you intend " +
        "to take, then call task_update as each step progresses or completes. The list is " +
        "re-rendered fresh each iteration so it survives context trimming. " +
        "Calling task_create REPLACES the existing list — to add a step you discovered later, " +
        "call task_create again with the full updated set of items. Returns the assigned ids " +
        "(1, 2, 3, …) you will use with task_update.")]
    public string TaskCreate(
        [Description("Ordered list of short imperative task descriptions, e.g. " +
                     "[\"Read the planning doc\", \"Search emails for context\", \"Draft response\"]. " +
                     "Empty array clears the list.")] string[] items)
    {
        _logger.LogInformation("Tool call: TaskCreate({Count} items)", items?.Length ?? 0);

        var created = _taskList.CreateOrReplace(items ?? []);
        if (created.Count == 0)
            return "Task list cleared.";

        var sb = new StringBuilder();
        sb.AppendLine($"Task list set ({created.Count} items):");
        foreach (var item in created)
            sb.AppendLine($"  {item.Id}. [{item.Status}] {item.Description}");
        return sb.ToString().TrimEnd();
    }

    [Description(
        "Update the status of a single task in the current task list. Statuses: " +
        "'pending' (not started), 'in_progress' (currently working), 'completed' (done). " +
        "Mark a task in_progress when you start it and completed when finished — keeping the " +
        "list accurate is what makes it useful across many iterations.")]
    public string TaskUpdate(
        [Description("The task id as returned by task_create (1, 2, 3, …)")] int id,
        [Description("New status: 'pending', 'in_progress', or 'completed'")] string status)
    {
        _logger.LogInformation("Tool call: TaskUpdate(id={Id}, status={Status})", id, status);

        if (string.IsNullOrWhiteSpace(status))
            return "Error: status is required (use 'pending', 'in_progress', or 'completed').";

        AgentTaskList.TaskItem? updated;
        try
        {
            updated = _taskList.Update(id, status);
        }
        catch (ArgumentException ex)
        {
            return $"Error: {ex.Message}";
        }

        if (updated is null)
        {
            var snapshot = _taskList.Snapshot();
            if (snapshot.Count == 0)
                return $"Error: no task with id {id} — the task list is empty. Call task_create first.";

            var ids = string.Join(", ", snapshot.Select(t => t.Id));
            return $"Error: no task with id {id}. Known ids: {ids}.";
        }

        return $"Task {updated.Id} → [{updated.Status}] {updated.Description}";
    }
}
