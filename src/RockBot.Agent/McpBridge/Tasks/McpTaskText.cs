using System.Text;
using RockBot.Agent.McpBridge.Handback;
using RockBot.Tools.Mcp.Elicitation;

namespace RockBot.Agent.McpBridge.Tasks;

/// <summary>
/// What the agent reads about a background task. Server-written text (a failure message, a
/// question) is untrusted and flattened, as in <see cref="HandbackText"/>.
/// </summary>
internal static class McpTaskText
{
    /// <summary>The tool result for a call that became a background task.</summary>
    public static string Started(string taskId, string server, string tool) =>
        $"[rockbot] {Flatten(server)}/{Flatten(tool)} started as a background task (task_id \"{taskId}\"). " +
        "It is still running. Its result will arrive on its own later, as a new message in this conversation: " +
        "don't wait for it, poll it, or call the tool again for the same request. If the user is waiting for it, " +
        "tell them it's underway. To stop it, call mcp_cancel_task(task_id: \"" + taskId + "\").";

    /// <summary>Why a call couldn't start a task because a cap was reached.</summary>
    public static string CapReached(string scope, int limit, IReadOnlyList<RunningMcpTask> sessionTasks)
    {
        var builder = new StringBuilder("[rockbot] This call wasn't started: ");
        builder.Append(scope == "session"
            ? $"this conversation already has {limit} background MCP task(s) running (the limit)."
            : $"the agent is already running {limit} background MCP task(s) (the limit).");

        var described = sessionTasks.Where(t => t.Entry is not null).ToList();
        if (described.Count > 0)
        {
            builder.Append(" Running here: ")
                .Append(string.Join("; ", described.Select(t =>
                    $"task_id \"{t.TaskId}\" ({Flatten(t.Entry!.Server)}/{Flatten(t.Entry.Tool)}, started {t.Entry.StartedAt:HH:mm} UTC)")))
                .Append('.');
        }

        builder.Append(" Wait for one to finish (its result arrives as a new message), or stop one that is no longer " +
                       "needed with mcp_cancel_task, then call the tool again.");
        return builder.ToString();
    }

    /// <summary>The text delivered when a task ends without a result.</summary>
    public static string Ended(McpTaskEntry entry)
    {
        var what = $"The background task \"{entry.TaskId}\" ({Flatten(entry.Server)}/{Flatten(entry.Tool)})";
        var reason = entry.Reason is { Length: > 0 } r ? Flatten(r).TrimEnd('.', ' ') : null;
        return entry.Status switch
        {
            McpTaskEntryStatus.Failed =>
                $"[rockbot] {what} failed on the server{(reason is null ? "" : $": {reason}")}. " +
                "If the result is still needed, decide whether to call the tool again.",
            McpTaskEntryStatus.Cancelled when entry.CancelledByAgent =>
                $"[rockbot] {what} was cancelled, as you asked.",
            McpTaskEntryStatus.Cancelled =>
                $"[rockbot] {what} was cancelled{(reason is null ? "" : $": {reason}")}. It produced no result.",
            McpTaskEntryStatus.Lost =>
                $"[rockbot] {what} was lost{(reason is null ? "" : $": {reason}")}. It produced no result. " +
                "If it is still needed, call the tool again.",
            McpTaskEntryStatus.TimedOut =>
                $"[rockbot] {what} ran longer than its {(int)Math.Round((entry.Deadline - entry.StartedAt).TotalMinutes)}-minute " +
                "limit, so it was cancelled. It produced no result.",
            _ => $"[rockbot] {what} ended ({entry.Status}){(reason is null ? "" : $": {reason}")}.",
        };
    }

    /// <summary>The text delivered with a question a task asked mid-run.</summary>
    public static string Question(McpTaskEntry entry, PendingQuestion question, bool formShown) =>
        $"[rockbot] The background task \"{entry.TaskId}\" ({Flatten(entry.Server)}/{Flatten(entry.Tool)}) is paused " +
        "until this question is answered. Answering lets it continue; its result will still arrive as a later message.\n" +
        HandbackText.Question(question, formShown);

    /// <summary>The reply to <c>mcp_answer</c> for a task's question.</summary>
    public static string AnswerSent(PendingQuestion question, string? taskId, bool declined) =>
        $"[rockbot] {(declined ? "Declined" : "Answer sent")}. The background task" +
        (taskId is null ? "" : $" \"{taskId}\"") +
        $" ({Flatten(question.ServerName)}/{Flatten(question.ToolName)}) continues, and its result will arrive " +
        "on its own as a later message. Don't wait for it or call the tool again.";

    private static string Flatten(string text) => McpElicitationSchemaDescriber.Flatten(text);
}
