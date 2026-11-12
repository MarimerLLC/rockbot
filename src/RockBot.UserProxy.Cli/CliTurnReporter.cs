using RockBot.UserProxy.Rendering;

namespace RockBot.UserProxy.Cli;

/// <summary>Process exit codes for one-shot chat, documented in the <c>--message</c> help text.</summary>
internal static class ChatExitCodes
{
    public const int Success = 0;
    public const int NoResponse = 2;
    public const int Cancelled = 3;
    public const int Error = 4;
    public const int AgentLost = 5;

    public const string HelpText =
        "Exit codes: 0 success, 2 no response (timeout), 3 cancelled, 4 error (any reply is still printed), 5 lost contact with the agent.";

    public static int For(TurnResultOutcome outcome) => outcome switch
    {
        TurnResultOutcome.Success => Success,
        TurnResultOutcome.Cancelled => Cancelled,
        TurnResultOutcome.Error => Error,
        TurnResultOutcome.AgentLost => AgentLost,
        _ => NoResponse,
    };

    /// <summary>The line describing a turn that didn't succeed, or null for one that did.</summary>
    public static string? Describe(TurnResult result) => result.Outcome switch
    {
        TurnResultOutcome.Success => null,
        TurnResultOutcome.Cancelled => "the agent cancelled the turn",
        TurnResultOutcome.Error => $"the agent reported an error{(result.Error is { Length: > 0 } e ? $": {e}" : "")}",
        TurnResultOutcome.AgentLost => $"lost contact with the agent{(result.Error is { Length: > 0 } e ? $" ({e})" : "")}",
        _ => "no reply received (timeout)",
    };
}

/// <summary>
/// Follows one turn for the CLI: formats its progress lines (structured tool calls when the
/// agent sends them) and keeps its task list, heartbeat status and latest progress for a spinner.
/// Thread-safe: the proxy calls it from its message-handling thread while the spinner reads it.
/// </summary>
internal sealed class CliTurnReporter(Action<string>? writeLine = null) : ITurnObserver, IProgress<AgentReply>
{
    private readonly Lock _lock = new();
    private readonly List<TaskListItem> _tasks = [];
    private string? _status;

    /// <summary>The latest heartbeat status or progress text.</summary>
    public string? Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>The compact task-list line (<c>[2/5] current item</c>), or null without a task list.</summary>
    public string? TaskLine
    {
        get { lock (_lock) return FormatTaskLine(_tasks); }
    }

    public void Report(AgentReply reply)
    {
        var line = FormatProgress(reply);
        lock (_lock) _status = reply.ToolCall is { } call ? FormatToolCall(call) : StripToLine(reply.Content);
        writeLine?.Invoke(line);
    }

    public void OnRunHeartbeat(RunHeartbeat heartbeat)
    {
        if (!string.IsNullOrWhiteSpace(heartbeat.Status))
            lock (_lock) _status = heartbeat.Status;
    }

    public void OnTaskListSnapshot(TaskListSnapshot snapshot)
    {
        string? line;
        lock (_lock)
        {
            _tasks.Clear();
            _tasks.AddRange(snapshot.Items);
            line = FormatTaskLine(_tasks);
        }
        if (line is not null)
            writeLine?.Invoke(line);
    }

    public void OnTaskListDelta(TaskListDelta delta)
    {
        string? line;
        lock (_lock)
        {
            var index = _tasks.FindIndex(t => t.Id == delta.Item.Id);
            if (index >= 0)
                _tasks[index] = delta.Item;
            else
                _tasks.Add(delta.Item);
            line = FormatTaskLine(_tasks);
        }
        if (line is not null)
            writeLine?.Invoke(line);
    }

    /// <summary>One stderr line for a progress reply: from its tool call when it has one, else its text.</summary>
    public static string FormatProgress(AgentReply reply) =>
        reply.ToolCall is { } call
            ? $"[{reply.AgentName}] {FormatToolCall(call)}"
            : $"[{reply.AgentName}] {HtmlPlainTextRenderer.StripHtml(reply.Content)}";

    /// <summary>A tool call as one line, with its duration and a failure marker.</summary>
    public static string FormatToolCall(ToolCallInfo call)
    {
        var duration = call.DurationMs is { } ms ? $" ({FormatDuration(ms)})" : "";
        return call.Phase switch
        {
            ToolCallPhase.Finished => string.IsNullOrWhiteSpace(call.Result)
                ? $"✓ {call.ToolCallName}{duration}"
                : $"✓ {call.ToolCallName} → {OneLine(call.Result)}{duration}",
            ToolCallPhase.Failed => $"✗ {call.ToolCallName} FAILED{duration}: {OneLine(call.Error ?? "unknown error")}",
            _ => string.IsNullOrWhiteSpace(call.ArgsSummary)
                ? $"→ {call.ToolCallName}…"
                : $"→ {call.ToolCallName}: {OneLine(call.ArgsSummary)}…",
        };
    }

    /// <summary><c>[done/total] current item</c>, or null for an empty list.</summary>
    public static string? FormatTaskLine(IReadOnlyList<TaskListItem> tasks)
    {
        if (tasks.Count == 0)
            return null;

        var done = tasks.Count(t => t.Status == "completed");
        var current = tasks.FirstOrDefault(t => t.Status == "in_progress")
            ?? tasks.FirstOrDefault(t => t.Status != "completed");
        return current is null
            ? $"[{done}/{tasks.Count}] all done"
            : $"[{done}/{tasks.Count}] {OneLine(current.Description)}";
    }

    private static string FormatDuration(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";

    private static string StripToLine(string content) => OneLine(HtmlPlainTextRenderer.StripHtml(content));

    private static string OneLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 200 ? line : line[..199] + "…";
    }
}
