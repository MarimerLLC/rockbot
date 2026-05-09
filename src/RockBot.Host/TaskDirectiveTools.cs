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

        Tools =
        [
            AIFunctionFactory.Create(UpdateTaskDirective)
        ];
    }

    public IList<AITool> Tools { get; }

    [Description(
        "Replace the entire body of the current scheduled task's evolving directive — the running " +
        "checklist or notes the agent maintains across runs of this task. The new content " +
        "becomes part of the system prompt on the next fire of this task. " +
        "Use this to record what to look for, what was just learned, or what to watch next time. " +
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
}
