using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Per-scheduled-task tool surface that lets the executing agent rewrite the task's evolving
/// directive body. The directive is injected as a system message on every fire of the task
/// (see <c>ScheduledTaskHandler</c>) — this is how a recurring task accumulates and refines
/// its own checklist over time without parking the content in the skill store.
///
/// Constructed per-fire so the task name is baked in and a tool call cannot accidentally
/// mutate a different task's directive.
/// </summary>
public sealed class TaskDirectiveTools
{
    private readonly IScheduledTaskStore _store;
    private readonly string _taskName;
    private readonly ILogger _logger;

    public TaskDirectiveTools(IScheduledTaskStore store, string taskName, ILogger logger)
    {
        _store = store;
        _taskName = taskName;
        _logger = logger;

        // Tool names are pinned to snake_case rather than inherited from the method
        // names: every prompt and directive in the repo refers to them that way, and
        // pointing the model at a name it cannot call is what produced issue #493.
        Tools =
        [
            AIFunctionFactory.Create(UpdateTaskDirective,
                new AIFunctionFactoryOptions { Name = "update_task_directive" }),
            AIFunctionFactory.Create(EditTaskDirective,
                new AIFunctionFactoryOptions { Name = "edit_task_directive" })
        ];
    }

    public IList<AITool> Tools { get; }

    [Description(
        "Replace the entire body of the current scheduled task's evolving directive — the running " +
        "checklist or notes the agent maintains across runs of this task. The new content " +
        "becomes part of the system prompt on the next fire of this task. " +
        "Use this to write the first version of a directive, or to deliberately start over. " +
        "To change part of an existing directive, use edit_task_directive instead — this tool " +
        "discards everything you do not restate. " +
        "This tool is only available inside a scheduled-task execution and only edits the " +
        "directive of the task that is currently running.")]
    public async Task<string> UpdateTaskDirective(
        [Description("The new directive content. Replaces the existing directive entirely.")] string content)
    {
        _logger.LogInformation(
            "Tool call: UpdateTaskDirective(task={Task}, length={Length})",
            _taskName, content?.Length ?? 0);

        await _store.UpdateDirectiveAsync(_taskName, content ?? string.Empty);
        return $"Directive for task '{_taskName}' updated ({(content ?? string.Empty).Length} chars). " +
               "It will be part of the system prompt on the next fire.";
    }

    [Description(
        "Change part of the current scheduled task's directive, leaving the rest of it exactly as it is. " +
        "This is how a recurring task adds a checklist item, corrects a note, or records what to watch " +
        "next time without restating the whole directive — which is what update_task_directive requires, " +
        "and what quietly loses the lines you forget. " +
        "The directive is shown to you in the system prompt on each fire; copy old_string from there " +
        "verbatim. If it appears more than once the edit is refused — include more surrounding text or " +
        "set replace_all.")]
    public async Task<string> EditTaskDirective(
        [Description("Exact text to find in the current directive — copy it verbatim")] string old_string,
        [Description("Replacement text. Pass an empty string to delete the matched text.")] string new_string,
        [Description("Replace every occurrence instead of refusing an ambiguous match. Default false.")] bool replace_all = false)
    {
        _logger.LogInformation(
            "Tool call: EditTaskDirective(task={Task}, replaceAll={ReplaceAll})", _taskName, replace_all);

        var result = await _store.EditDirectiveAsync(
            _taskName, old_string ?? string.Empty, new_string ?? string.Empty, replace_all);

        if (!result.IsSuccess)
            return $"Edit failed on the directive for task '{_taskName}': {result.Error}";

        var plural = result.ReplacementCount == 1 ? "occurrence" : "occurrences";
        return $"Directive for task '{_taskName}' edited — replaced {result.ReplacementCount} {plural} " +
               $"({result.OldLength} → {result.NewLength} chars). " +
               "It will be part of the system prompt on the next fire.";
    }
}
