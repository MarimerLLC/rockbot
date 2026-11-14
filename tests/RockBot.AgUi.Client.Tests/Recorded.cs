using System.Text;
using AGUI.Abstractions;

namespace RockBot.AgUi.Client.Tests;

/// <summary>
/// Event streams recorded from the real proxy (<c>RockBot.UserProxy.AgUi</c> behind the dev host,
/// with the demo agent), as it wrote them, lightly edited: ids are shortened, and the first run
/// was given a keepalive comment, an attachment and an "add" patch so one stream covers them.
/// Where a stream is marked "composed", the proxy's events were put together by hand from the
/// shapes in <c>docs/agui.md</c>, because the dev host has no quick way to produce them.
/// </summary>
internal static class Recorded
{
    public const string Thread = "agui-session";

    /// <summary>A run with progress, a tool call, a task list that is then completed, and the reply.</summary>
    public const string RunWithToolCallAndTaskList = """
        data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-1","protocolVersion":"1.0","timestamp":1794689046142}

        data: {"type":"CUSTOM","name":"rockbot.agent_run_started","value":{"agentName":"RockBot","heartbeatIntervalSeconds":10},"timestamp":1794689046161}

        data: {"type":"ACTIVITY_SNAPSHOT","messageId":"run-1-progress","activityType":"rockbot.progress","content":{"agentName":"RockBot","text":"Thinking…"},"replace":true,"timestamp":1794689046180}

        data: {"type":"STATE_SNAPSHOT","snapshot":{"taskLists":{"primary":[{"id":"1","description":"Look it up","status":"in_progress"},{"id":"2","description":"Write the answer","status":"pending"}]}},"timestamp":1794689046182}

        data: {"type":"TOOL_CALL_START","toolCallId":"call-633ad0ef","toolCallName":"web_search","timestamp":1794689046183}

        data: {"type":"TOOL_CALL_ARGS","toolCallId":"call-633ad0ef","delta":"{\"summary\":\"query: Hello\"}","timestamp":1794689046183}

        data: {"type":"TOOL_CALL_END","toolCallId":"call-633ad0ef","timestamp":1794689046183}

        : keepalive

        data: {"type":"TOOL_CALL_RESULT","messageId":"6d9a03cf","toolCallId":"call-633ad0ef","content":"3 results","role":"tool","timestamp":1794689046323,"metadata":{"rockbot":{"status":"finished","failed":false,"durationMs":150,"agentName":"RockBot"}}}

        data: {"type":"STATE_DELTA","delta":[{"op":"replace","path":"/taskLists/primary/0","value":{"id":"1","description":"Look it up","status":"completed"}}],"timestamp":1794689046325}

        data: {"type":"STATE_DELTA","delta":[{"op":"add","path":"/taskLists/primary/-","value":{"id":"3","description":"Check it","status":"pending"}}],"timestamp":1794689046325}

        data: {"type":"TEXT_MESSAGE_START","messageId":"2f3dead3","role":"assistant","name":"RockBot","timestamp":1794689046328}

        data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"2f3dead3","delta":"Echo: Hello","timestamp":1794689046328}

        data: {"type":"TEXT_MESSAGE_END","messageId":"2f3dead3","timestamp":1794689046328}

        data: {"type":"CUSTOM","name":"rockbot.attachments","value":{"messageId":"2f3dead3","attachments":[{"mime":"image/png","path":"charts/q3 sales.png","fileName":"sales.png","url":"/attachments/charts/q3%20sales.png"}]},"timestamp":1794689046329}

        data: {"type":"RUN_FINISHED","threadId":"agui-session","runId":"run-1","timestamp":1794689046329}


        """;

    /// <summary>A run the agent never started: the proxy stops waiting and says the reply is still to come.</summary>
    public const string RunThatTimedOut = """
        data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-silent","protocolVersion":"1.0","timestamp":1794689046608}

        data: {"type":"CUSTOM","name":"rockbot.reply_pending","value":{"runId":"run-silent","runStarted":false,"message":"no reply received (timeout)"},"timestamp":1794689048617}

        data: {"type":"RUN_FINISHED","threadId":"agui-session","runId":"run-silent","timestamp":1794689048617}


        """;

    /// <summary>
    /// Composed: what the notify stream carries for that run afterwards. The agent starts on it
    /// late, reports a task list and a heartbeat, replies, and finishes.
    /// </summary>
    public const string NotifyLateReply = """
        id: 5f0c-1
        data: {"type":"CUSTOM","name":"rockbot.agent_run_started","value":{"runId":"run-silent","agentName":"RockBot","heartbeatIntervalSeconds":10},"timestamp":1794689050000}

        id: 5f0c-2
        data: {"type":"STATE_SNAPSHOT","snapshot":{"taskLists":{"primary":[{"id":"1","description":"Catch up","status":"in_progress"}]}},"timestamp":1794689050500}

        id: 5f0c-3
        data: {"type":"CUSTOM","name":"rockbot.heartbeat","value":{"runId":"run-silent","elapsedSeconds":10,"status":"Reading the report"},"timestamp":1794689060000}

        id: 5f0c-4
        data: {"type":"TEXT_MESSAGE_START","messageId":"late-1","role":"assistant","name":"RockBot","timestamp":1794689061000}

        id: 5f0c-5
        data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"late-1","delta":"Sorry for the wait. Here it is.","timestamp":1794689061000}

        id: 5f0c-6
        data: {"type":"TEXT_MESSAGE_END","messageId":"late-1","timestamp":1794689061000}

        id: 5f0c-7
        data: {"type":"CUSTOM","name":"rockbot.origin","value":{"messageId":"late-1","sessionId":"agui-session","source":{"kind":"primary"}},"timestamp":1794689061000}

        id: 5f0c-8
        data: {"type":"CUSTOM","name":"rockbot.run_finished","value":{"runId":"run-silent","outcome":"success"},"timestamp":1794689061100}


        """;

    /// <summary>
    /// A subagent's whole life (its first progress arrives before its start event, as it did),
    /// then a scheduled task's report with its origin.
    /// </summary>
    public const string NotifySubagentAndScheduledReply = """
        id: 636e-1
        data: {"type":"ACTIVITY_SNAPSHOT","subagentRunId":"t36d90f","messageId":"notify-progress-t36d90f","activityType":"rockbot.progress","content":{"agentName":"subagent-t36d90f","text":"Reading the price list"},"replace":true,"timestamp":1794689046426}

        id: 636e-2
        data: {"type":"SUBAGENT_STARTED","subagentRunId":"t36d90f","name":"subagent","description":"Research the vendors","timestamp":1794689046424,"rawEvent":{"subagentRunId":"t36d90f","kind":"subagent","description":"Research the vendors","threadId":"agui-session","startedAt":"2026-11-14T20:44:06.4241574+00:00"}}

        id: 636e-3
        data: {"type":"STATE_SNAPSHOT","snapshot":{"taskLists":{"t36d90f":[{"id":"1","description":"Compare prices","status":"in_progress"}]}},"timestamp":1794689046429}

        id: 636e-4
        data: {"type":"ACTIVITY_SNAPSHOT","subagentRunId":"t36d90f","messageId":"notify-progress-t36d90f","activityType":"rockbot.progress","content":{"agentName":"subagent-t36d90f","text":"","done":true},"replace":true,"timestamp":1794689046729}

        id: 636e-5
        data: {"type":"TEXT_MESSAGE_START","subagentRunId":"t36d90f","messageId":"cd433eaf","role":"assistant","name":"subagent-t36d90f","timestamp":1794689046729}

        id: 636e-6
        data: {"type":"TEXT_MESSAGE_CONTENT","subagentRunId":"t36d90f","messageId":"cd433eaf","delta":"Vendor B is the cheapest by 12%.","timestamp":1794689046729}

        id: 636e-7
        data: {"type":"TEXT_MESSAGE_END","subagentRunId":"t36d90f","messageId":"cd433eaf","timestamp":1794689046729}

        id: 636e-8
        data: {"type":"CUSTOM","name":"rockbot.origin","value":{"messageId":"cd433eaf","sessionId":"agui-session","origin":{"channel":"agui","promptSummary":"Hello notify","startedAt":"2026-11-14T20:44:06.4241574+00:00","sessionId":"agui-session"},"source":{"kind":"subagent","taskId":"t36d90f"}},"timestamp":1794689046730}

        id: 636e-9
        data: {"type":"SUBAGENT_FINISHED","subagentRunId":"t36d90f","timestamp":1794689046727,"rawEvent":{"subagentRunId":"t36d90f","kind":"subagent","threadId":"agui-session","outcome":"success","finishedAt":"2026-11-14T20:44:06.7271325+00:00"}}

        id: 636e-10
        data: {"type":"STATE_SNAPSHOT","snapshot":{"taskLists":{}},"timestamp":1794689046732}

        id: 636e-11
        data: {"type":"TEXT_MESSAGE_START","messageId":"08cb523c","role":"assistant","name":"RockBot","timestamp":1794689046733}

        id: 636e-12
        data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"08cb523c","delta":"**Daily summary.** Nothing needs your attention today.","timestamp":1794689046733}

        id: 636e-13
        data: {"type":"TEXT_MESSAGE_END","messageId":"08cb523c","timestamp":1794689046733}

        id: 636e-14
        data: {"type":"CUSTOM","name":"rockbot.origin","value":{"messageId":"08cb523c","sessionId":"scheduled","origin":{"channel":"scheduler","promptSummary":"Daily summary","startedAt":"2026-11-14T20:39:06.4241574+00:00","sessionId":"scheduled"},"source":{"kind":"scheduled"}},"timestamp":1794689046733}


        """;

    /// <summary>The value of a <c>rockbot.mcp_question</c> event, with the interrupt left out (custom mode doesn't read it).</summary>
    public const string QuestionValue = """
        {"sessionId":"agui-session","agentName":"mcp-bridge","form":{"questionId":"q-bde073c5","serverName":"scheduler","toolName":"create_event","message":"Which time zone should the meeting be in?","expiresAt":"2999-11-14T20:54:06.3393531+00:00","fields":[{"name":"tz","kind":"string","title":"Time zone","description":null,"required":true,"isDecision":false,"options":null,"minimum":null,"maximum":null,"minLength":null,"maxLength":null,"minItems":null,"maxItems":null,"format":null,"default":null},{"name":"length","kind":"single-select","title":"Length","description":null,"required":false,"isDecision":false,"options":[{"value":"30","label":"30 minutes"},{"value":"60","label":"One hour"}],"minimum":null,"maximum":null,"minLength":null,"maxLength":null,"minItems":null,"maxItems":null,"format":null,"default":"\"30\""},{"name":"notify","kind":"boolean","title":"Email the attendees","description":null,"required":false,"isDecision":false,"options":null,"minimum":null,"maximum":null,"minLength":null,"maxLength":null,"minItems":null,"maxItems":null,"format":null,"default":null}]},"text":"scheduler asks: Which time zone should the meeting be in?"}
        """;

    /// <summary>A run in custom question mode: the question is an event in the run, which then ends plainly.</summary>
    public static readonly string RunWithQuestion = $$"""
        data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-2","protocolVersion":"1.0","timestamp":1794689046338}

        data: {"type":"CUSTOM","name":"rockbot.mcp_question","value":{{QuestionValue.Trim()}},"timestamp":1794689046358}

        data: {"type":"TEXT_MESSAGE_START","messageId":"16d3d24c","role":"assistant","name":"RockBot","timestamp":1794689046598}

        data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"16d3d24c","delta":"The scheduler needs a time zone; please fill in the form.","timestamp":1794689046598}

        data: {"type":"TEXT_MESSAGE_END","messageId":"16d3d24c","timestamp":1794689046598}

        data: {"type":"RUN_FINISHED","threadId":"agui-session","runId":"run-2","timestamp":1794689046598}


        """;

    /// <summary>What the notify stream carries after the form is answered: its status, then the agent's reply.</summary>
    public const string NotifyQuestionAnswered = """
        id: 636e-20
        data: {"type":"CUSTOM","name":"rockbot.mcp_question_status","value":{"sessionId":"agui-session","questionId":"q-bde073c5","state":"answered","answeredBy":"user"},"timestamp":1794689047000}

        id: 636e-21
        data: {"type":"TEXT_MESSAGE_START","messageId":"b02d","role":"assistant","name":"RockBot","timestamp":1794689047100}

        id: 636e-22
        data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"b02d","delta":"Booked for Brussels time.","timestamp":1794689047100}

        id: 636e-23
        data: {"type":"TEXT_MESSAGE_END","messageId":"b02d","timestamp":1794689047100}

        id: 636e-24
        data: {"type":"CUSTOM","name":"rockbot.origin","value":{"messageId":"b02d","sessionId":"agui-session","source":{"kind":"primary"},"followsQuestionId":"q-bde073c5"},"timestamp":1794689047100}


        """;

    /// <summary><c>GET /threads/{id}/messages</c>, as the proxy answered it.</summary>
    public const string History = """
        {"type":"MESSAGES_SNAPSHOT","messages":[{"id":"agui-session-0","role":"user","metadata":{"rockbot":{"timestamp":"2026-11-14T20:44:06.1608981+00:00"}},"content":"an image please"},{"role":"assistant","name":"RockBot","content":"Here is an image.","id":"agui-session-1","metadata":{"rockbot":{"timestamp":"2026-11-14T20:44:06.6037887+00:00","agentName":"RockBot","attachments":[{"mime":"image/png","path":"demo-fd34.png","fileName":"demo.png","url":"/attachments/demo-fd34.png"}]}}}],"timestamp":1794689048626}
        """;

    /// <summary>The frames of a recorded stream, read by the client's own reader.</summary>
    public static async Task<List<AgUiFrame>> FramesAsync(string sse)
    {
        var frames = new List<AgUiFrame>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Normalize(sse)));
        await foreach (var frame in AgUiSse.ReadAsync(stream))
            frames.Add(frame);
        return frames;
    }

    public static async Task<List<BaseEvent>> EventsAsync(string sse) =>
        [.. (await FramesAsync(sse)).Select(f => f.Event)];

    /// <summary>Raw string literals carry the source file's line endings; the wire uses <c>\n</c>.</summary>
    public static string Normalize(string sse) => sse.Replace("\r\n", "\n");
}
