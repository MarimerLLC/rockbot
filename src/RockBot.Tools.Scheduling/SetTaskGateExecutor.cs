using System.Text.Json;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Tools.Scheduling;

/// <summary>
/// Executes the <c>set_task_gate</c> tool (#848): switches a saved wisp on (or off) as an existing
/// task's gate. On every fire the wisp runs first, with no LLM; when it finds nothing new the LLM
/// turn is skipped. Everything else about the task is kept, which re-scheduling it with
/// <c>schedule_task</c> would not do.
/// </summary>
/// <param name="skills">Checks the wisp resource exists. Optional: without it the check is skipped.</param>
/// <param name="wisps">Checks the resource is a valid wisp. Optional: without it the check is skipped.</param>
internal sealed class SetTaskGateExecutor(
    ISchedulerService scheduler,
    ISkillStore? skills = null,
    ISavedWispRunner? wisps = null) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        string? name, skill, resource, modeText;
        int? maxSkips;
        bool clear;
        try
        {
            var args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
            name = Str(args, "name");
            skill = Str(args, "skill");
            resource = Str(args, "resource");
            modeText = Str(args, "mode");
            maxSkips = args.TryGetValue("maxConsecutiveSkips", out var m) && m.ValueKind == JsonValueKind.Number
                ? m.GetInt32()
                : null;
            clear = args.TryGetValue("clear", out var c) && c.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return Response(request, $"Invalid arguments: {ex.Message}", isError: true);
        }

        if (string.IsNullOrWhiteSpace(name))
            return Response(request, "Missing required argument: name", isError: true);

        try
        {
            var tasks = await scheduler.ListAsync(ct);
            var task = tasks.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (task is null)
            {
                var known = tasks.Count == 0 ? "none" : string.Join(", ", tasks.Select(t => t.Name));
                return Response(request, $"No scheduled task named '{name}'. Scheduled tasks: {known}.", isError: true);
            }

            if (clear)
            {
                if (task.Gate is null)
                    return Response(request, $"Task '{task.Name}' has no gate; nothing changed.", isError: false);
                await scheduler.ScheduleAsync(task with { Gate = null }, ct);
                return Response(request,
                    $"Removed the gate ({task.Gate.Target}) from task '{task.Name}'. It runs its full LLM turn on every fire again.",
                    isError: false);
            }

            if (string.IsNullOrWhiteSpace(skill) || string.IsNullOrWhiteSpace(resource))
                return Response(request,
                    "Missing required arguments: skill and resource name the saved wisp to run (or pass clear: true).",
                    isError: true);

            TaskGateMode mode;
            if (string.IsNullOrWhiteSpace(modeText))
                mode = TaskGateMode.NonEmpty;
            else if (!TryParseMode(modeText, out mode))
                return Response(request, $"Unknown mode '{modeText}'. Use 'nonEmpty' or 'changed'.", isError: true);

            var note = "";
            if (skills is not null)
            {
                var body = await skills.GetResourceAsync(skill, resource);
                if (body is null)
                    return Response(request,
                        $"Skill '{skill}' has no resource '{resource}'. Save the wisp on the skill first.", isError: true);

                if (wisps is not null)
                {
                    var check = wisps.Inspect(body);
                    if (!check.Valid)
                        return Response(request,
                            $"'{skill}/{resource}' is not a valid wisp: {check.Reason}", isError: true);
                    if (!check.ReadOnly)
                        note = $" Warning: this wisp is not read-only ({check.Reason}); it runs on every fire, before any judgement.";
                }
            }

            var gate = new TaskGate(
                skill.Trim(), resource.Trim(), mode,
                maxSkips ?? TaskGate.DefaultMaxConsecutiveSkips,
                EnabledBy: "user",
                Reason: "set with set_task_gate");
            await scheduler.ScheduleAsync(task with { Gate = gate }, ct);

            var forced = gate.MaxConsecutiveSkips > 0
                ? $" After {gate.MaxConsecutiveSkips} skipped runs in a row it runs anyway."
                : " It never forces a run.";
            var when = mode == TaskGateMode.Changed
                ? "the wisp's results differ from the last run's"
                : "the wisp returns any results";
            return Response(request,
                $"Task '{task.Name}' now runs the wisp {gate.Target} first on every fire, and its LLM turn runs only when {when}." +
                forced + " A failed wisp lets the turn run. Cron, description, directive and output format are unchanged." + note,
                isError: false);
        }
        catch (Exception ex)
        {
            return Response(request, $"Failed to set the task's gate: {ex.Message}", isError: true);
        }
    }

    internal static bool TryParseMode(string text, out TaskGateMode mode)
    {
        var normalized = text.Replace("-", "").Replace("_", "").Trim();
        return Enum.TryParse(normalized, ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }

    private static string? Str(Dictionary<string, JsonElement> args, string key) =>
        args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ToolInvokeResponse Response(ToolInvokeRequest request, string content, bool isError) => new()
    {
        ToolCallId = request.ToolCallId,
        ToolName = request.ToolName,
        Content = content,
        IsError = isError
    };
}
