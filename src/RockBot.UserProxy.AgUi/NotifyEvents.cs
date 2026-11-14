using System.Text.Json;
using AGUI.Abstractions;

namespace RockBot.UserProxy.AgUi;

/// <summary>
/// What each piece of unsolicited traffic looks like on the notify stream. Replies, progress,
/// subagent lifecycle and task lists use the same <see cref="TurnEventMapper"/> methods a run
/// does; this adds the events only the notify stream has, all <c>CUSTOM rockbot.*</c>.
/// </summary>
internal static class NotifyEvents
{
    /// <summary>Stands where a run id would in ids the mapper derives. The notify stream is not a run.</summary>
    public const string StreamName = "notify";

    /// <summary>
    /// A final reply nobody is waiting for. A handed-back MCP question becomes
    /// <c>rockbot.mcp_question</c> with its form; anything else is a text message followed by
    /// <c>rockbot.origin</c>.
    /// </summary>
    public static IEnumerable<BaseEvent> Reply(TurnEventMapper mapper, AgentReply reply)
    {
        if (!string.Equals(reply.ContentType, McpQuestionForm.ContentType, StringComparison.OrdinalIgnoreCase))
            return mapper.UnsolicitedReply(reply);

        return
        [
            mapper.Custom(TurnEventMapper.CustomNames.McpQuestion, new
            {
                reply.SessionId,
                reply.AgentName,
                // The form as the agent sent it, so a field this proxy doesn't know still arrives.
                form = ParseJson(reply.StructuredData),
                // The same question as plain text, for a client that can't show the form.
                text = reply.Content,
            }),
        ];
    }

    /// <summary>
    /// A non-final reply nobody is waiting for: progress from a subagent, a worker, a remote agent
    /// or a turn this proxy gave up on. Progress is an activity (or tool-call events) keyed by its
    /// source. A completion is a subagent's result: it closes that source's activity and is then
    /// delivered as a message, like any other unsolicited reply.
    /// </summary>
    public static IEnumerable<BaseEvent> Status(TurnEventMapper mapper, AgentReply reply)
    {
        var activityId = ActivityId(reply);
        if (!reply.IsCompletion)
            return mapper.Progress(reply, activityId);

        return [mapper.ActivityDone(reply, activityId), .. mapper.UnsolicitedReply(reply)];
    }

    /// <summary>
    /// One activity per source: a subagent or worker by its task id, anything else by the name of
    /// the agent that reported it.
    /// </summary>
    public static string ActivityId(AgentReply reply) =>
        reply.Source is { Kind: ReplySourceKind.Subagent or ReplySourceKind.Worker, TaskId: { Length: > 0 } taskId }
            ? $"{StreamName}-progress-{taskId}"
            : $"{StreamName}-progress-{reply.AgentName}";

    public static IEnumerable<BaseEvent> SubagentFinished(TurnEventMapper mapper, SubagentFinished finished)
    {
        yield return mapper.SubagentFinished(finished);

        // Its task list goes with it, or the thread's state would grow for as long as the proxy runs.
        if (mapper.ForgetTaskList(finished.SubagentRunId) is { } state)
            yield return state;
    }

    // Run events for a turn this proxy stopped waiting for. In a run's own stream the run id is
    // implied; here it has to be said.

    public static BaseEvent RunStarted(TurnEventMapper mapper, RunStarted run) => mapper.Custom(
        TurnEventMapper.CustomNames.AgentRunStarted,
        new { run.RunId, run.AgentName, run.HeartbeatIntervalSeconds },
        run.Timestamp);

    public static BaseEvent RunHeartbeat(TurnEventMapper mapper, RunHeartbeat heartbeat) => mapper.Custom(
        TurnEventMapper.CustomNames.Heartbeat,
        new { heartbeat.RunId, heartbeat.ElapsedSeconds, heartbeat.Status },
        heartbeat.Timestamp);

    public static BaseEvent RunFinished(TurnEventMapper mapper, RunFinished run) => mapper.Custom(
        TurnEventMapper.CustomNames.RunFinished,
        new { run.RunId, run.Outcome, run.Error },
        run.Timestamp);

    public static BaseEvent McpQuestionStatus(TurnEventMapper mapper, McpQuestionStatus status) =>
        mapper.Custom(TurnEventMapper.CustomNames.McpQuestionStatus, status);

    public static BaseEvent McpSignInStatus(TurnEventMapper mapper, McpSignInStatus status) =>
        mapper.Custom(TurnEventMapper.CustomNames.McpSignInStatus, status);

    public static BaseEvent AgentName(TurnEventMapper mapper, string agentName) =>
        mapper.Custom(TurnEventMapper.CustomNames.AgentName, new { agentName });

    public static BaseEvent Error(TurnEventMapper mapper, string message) =>
        mapper.Custom(TurnEventMapper.CustomNames.Error, new { message });

    /// <summary>
    /// The agent's running background work, which it broadcasts when it starts (empty, so a client
    /// still showing work from before the restart clears it). Each thread gets only its own.
    /// </summary>
    public static BaseEvent ActiveWork(TurnEventMapper mapper, ActiveStatusResponse snapshot) => mapper.Custom(
        TurnEventMapper.CustomNames.ActiveWork,
        new
        {
            work = (snapshot.Work ?? [])
                .Where(w => string.Equals(w.ThreadId, mapper.ThreadId, StringComparison.Ordinal))
                .ToList(),
        });

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
