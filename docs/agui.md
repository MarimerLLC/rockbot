---
title: AG-UI proxy
nav_order: 18
---

# AG-UI proxy (`RockBot.UserProxy.AgUi`)

The AG-UI proxy exposes the agent over [AG-UI 1.0](https://docs.ag-ui.com/), an open protocol
between an agent backend and a user-facing app. A client sends an HTTP POST and reads the turn back
as server-sent events (SSE). It is for clients that can't open a RabbitMQ connection: a Blazor
WebAssembly app, a phone app, or a third-party AG-UI front end such as CopilotKit or the .NET
`AGUIChatClient`.

It is a user proxy like the [CLI](cli) and the [Blazor UI](blazor-ui): it owns a `UserProxyService`
and talks to the agent on the same `user.*` topics. Its user interface runs somewhere else and
reaches it over HTTP.

```
client ──POST /agui─────────► RockBot.UserProxy.AgUi ──RabbitMQ──► agent
       ◄──── SSE (a run) ────                        ◄───────────
       ◄──── SSE (notify) ───
```

This page describes what is built so far: runs, the notify stream for output nobody asked for
(subagent results, scheduled tasks, late replies), MCP questions (as AG-UI interrupts or as
RockBot's own events), cancelling, and history. Attachments, sign-in and CORS come in later phases of
[#794](https://github.com/MarimerLLC/rockbot/issues/794). The full design is in
[`design/userproxy-agui.md`](https://github.com/MarimerLLC/rockbot/blob/main/design/userproxy-agui.md).

> **The proxy has no sign-in of its own yet.** Whoever can reach it can talk to the agent as its
> user. Run it on a private network such as a tailnet, or behind a gateway that authenticates. It
> sends no CORS headers either, so a browser app on another origin can't call it directly yet.

## Endpoints

| Endpoint | Does |
|---|---|
| `POST /agui` | Runs one turn. The body is an AG-UI `RunAgentInput`; the response is the run's event stream. |
| `GET /agui/notify?threadId=` | The thread's unsolicited output, as a stream of AG-UI events that stays open. |
| `POST /questions/{id}/answer` | Answers or declines an MCP question. Answers `202`. See [MCP questions](#mcp-questions). |
| `POST /threads/{id}/cancel` | Asks the agent to stop the thread's work. Answers `202`. |
| `GET /threads/{id}/messages` | The thread's conversation history, as one `MESSAGES_SNAPSHOT` event in JSON. |
| `GET /healthz` | `200` while the proxy is subscribed on the message bus, `503` otherwise. |

### A run: `POST /agui`

```bash
curl -N -X POST http://rockbot-agui/agui \
  -H 'Content-Type: application/json' \
  -d '{
        "threadId": "agui-session",
        "runId": "run-0001",
        "messages": [ { "id": "m1", "role": "user", "content": "What is on my calendar today?" } ]
      }'
```

```
data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-0001","protocolVersion":"1.0","timestamp":1794547308650}

data: {"type":"CUSTOM","name":"rockbot.agent_run_started","value":{"agentName":"RockBot","heartbeatIntervalSeconds":15},…}

data: {"type":"TOOL_CALL_START","toolCallId":"call_p6od…","toolCallName":"calendar__list_events",…}

data: {"type":"TEXT_MESSAGE_START","messageId":"1939…","role":"assistant","name":"RockBot",…}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"1939…","delta":"You have two meetings today…",…}

data: {"type":"TEXT_MESSAGE_END","messageId":"1939…",…}

data: {"type":"RUN_FINISHED","threadId":"agui-session","runId":"run-0001",…}
```

What the proxy reads from the request:

- **`threadId`** is the RockBot session. It must be one of the allowed thread ids (see
  [Configuration](#configuration)).
- **`runId`** identifies the run. It is also the turn's correlation id on the bus, so the agent's
  logs and run events carry it. Use a new one for each run.
- **`messages`**: only the text of the **last user message** is sent. The agent keeps the
  conversation history itself, so anything else the client replays is ignored. Image and document
  parts of a message are dropped for now.
- **`forwardedProps.rockbot.capabilities`** (optional) says what the client can render, as an array
  of [`ClientCapabilities`](https://github.com/MarimerLLC/rockbot/blob/main/src/RockBot.UserProxy.Abstractions/ClientCapabilities.cs)
  names, for example `["MarkdownBasic", "MarkdownTables", "LinkInline"]`. Names are
  case-insensitive, and names the proxy doesn't know are ignored. Without the array the agent
  assumes text, every Markdown subset, inline links and image attachments, and no inline HTML, SVG
  or forms.
- **`forwardedProps.rockbot.questionMode`** (optional), `"interrupt"` or `"custom"`, asks for MCP
  questions as forms and says how the client takes them. See [MCP questions](#mcp-questions).
- **`resume`** answers the interrupts a thread has open. A run with `resume` sends no message; see
  [MCP questions](#mcp-questions).
- `tools`, `state` and `context` are ignored.

The stream is the same sequence of events that `rockbot chat --output jsonl` writes, from the same
code; the [CLI page](cli#json-lines---output-jsonl) lists every event and its fields. In short:

- The first event is always `RUN_STARTED` and the last is always `RUN_FINISHED` or `RUN_ERROR`.
- The reply arrives whole, as one `TEXT_MESSAGE_START` / `CONTENT` / `END` group. The agent doesn't
  stream tokens.
- Progress is `ACTIVITY_SNAPSHOT` (text) and `TOOL_CALL_*` (tool calls); the agent's task list is
  `STATE_SNAPSHOT` / `STATE_DELTA`.
- A reply's attachments are listed in `CUSTOM rockbot.attachments`, by path. The proxy can't serve
  their bytes yet.
- Subagent and worker events (`SUBAGENT_*`, their progress and their results) are not in a run's
  stream. They are on the [notify stream](#the-notify-stream-get-aguinotify), with everything
  else that isn't the run's own reply.
- A cancelled run ends with `RUN_FINISHED` and `outcome.type: "cancelled"`.
- A run in `interrupt` mode that leaves an MCP question open ends with `RUN_FINISHED` and
  `outcome.type: "interrupt"`. See [MCP questions](#mcp-questions).
- **A run the proxy stops waiting for is not an error.** When the agent doesn't start the run within
  `UserProxy:ReplyTimeout`, or the run passes `UserProxy:MaxTurnDuration`, the agent may still be
  working. The run then ends with `CUSTOM rockbot.reply_pending` (its value has `runId`,
  `runStarted` and a `message`) followed by a plain `RUN_FINISHED`, and the reply arrives later on
  the notify stream. A client without a notify stream finds the reply in the thread's history. Here
  the proxy differs from `rockbot chat --output jsonl`, which ends such a turn with `RUN_ERROR` and
  code `timeout`.
- `RUN_ERROR.code` is one of:

  | Code | Meaning |
  |---|---|
  | `error` | The agent reported that the turn failed. |
  | `agent_lost` | The run started, then the agent went silent. For a run with `resume`: the agent never acknowledged an answer. |
  | `interrupts_pending` | New input in `interrupt` mode while the thread has open interrupts. Resume them first. |
  | `resume_incomplete` | `resume` doesn't answer every open interrupt. Nothing was answered. |
  | `interrupt_rejected` | The agent refused an answer. The interrupt is still open; resume it again. |
  | `client_error` | The proxy itself failed, for example it could not publish to the bus. |
  | `proxy_stopping` | The proxy is shutting down (a rollout). The agent finishes the turn; read the reply from the thread's history. |

While nothing else is being sent, the proxy writes an SSE comment (`: keepalive`) every 15 seconds,
so proxies and mobile networks don't drop the connection. SSE clients skip comments.

A request the proxy can't run is refused with an ordinary HTTP error (a problem-details JSON body)
before any event is sent:

| Status | Why |
|---|---|
| `400` | The body isn't a `RunAgentInput`, has no `threadId` or `runId`, has no user message with text (and no `resume`), names an unknown `questionMode`, or has a malformed `resume` entry: no `interruptId`, a `status` other than `resolved` or `cancelled`, a `resolved` entry whose `payload` isn't an object, or two entries for one interrupt. |
| `403` | The `threadId` is not allowed. |
| `409` | A run with this `runId` is still in progress, or another run is already resuming one of the interrupts. |
| `503` | The proxy is not connected to the message bus. |

### The notify stream: `GET /agui/notify`

AG-UI has no way for a server to send something the client didn't ask for, and RockBot produces
plenty: subagent results, scheduled-task output, inbound A2A notifications, question forms, the
late reply of a run that timed out. The notify stream carries all of it. Keep one open for as long
as the app is in use:

```bash
curl -N 'http://rockbot-agui/agui/notify?threadId=agui-session'
```

```
id: 5f0c2a9e-0
data: {"type":"CUSTOM","name":"rockbot.connected","value":{"threadId":"agui-session"},"timestamp":1794547308650}

id: 5f0c2a9e-1
data: {"type":"SUBAGENT_STARTED","subagentRunId":"t1","name":"subagent","description":"Research the vendors",…}

id: 5f0c2a9e-2
data: {"type":"ACTIVITY_SNAPSHOT","messageId":"notify-progress-t1","activityType":"rockbot.progress","content":{"agentName":"subagent-t1","text":"Reading the price list"},"replace":true,"subagentRunId":"t1",…}

id: 5f0c2a9e-3
data: {"type":"TEXT_MESSAGE_START","messageId":"8d1e…","role":"assistant","name":"RockBot",…}
```

Each `data:` line is one AG-UI event, in the same JSON a run uses, so a client needs one parser for
both. The stream is not a run, though: it has no `RUN_STARTED` and no `RUN_FINISHED`, and it never
ends by itself. It ends when the client closes it, when the proxy stops (a rollout), or when the
client falls too far behind (see [Reconnecting](#reconnecting)). Like a run, an idle stream gets a
`: keepalive` comment every 15 seconds.

`400` without `threadId`, and `403` for a thread that isn't allowed.

#### What arrives

| What happened | Events |
|---|---|
| A reply nobody is waiting for: a scheduled task's output, the agent's answer after a subagent finished, an A2A result, a late reply, the reply to a form answer that no run is waiting for | `TEXT_MESSAGE_START` / `CONTENT` / `END`, `CUSTOM rockbot.attachments` when it has any, then `CUSTOM rockbot.origin` with `messageId`, `origin` (`channel`, `promptSummary`, `startedAt`, `sessionId`), `source`, and `followsQuestionId` on the reply to a form answer |
| An MCP server's question, as a form, when no run on the thread took it | `CUSTOM rockbot.mcp_question`: `form` (the [`McpQuestionForm`](https://github.com/MarimerLLC/rockbot/blob/main/src/RockBot.UserProxy.Abstractions/McpQuestionForm.cs)), `text` (the same question as plain text), `interrupt` (the question as an AG-UI interrupt), `sessionId`, `agentName`. Only a thread whose last message asked for forms gets them. See [MCP questions](#mcp-questions). |
| A question changed state | `CUSTOM rockbot.mcp_question_status`: `sessionId`, `questionId`, `state` (`answered`, `declined`, `expired`, `cancelled`, `interrupted`, `unavailable`, or `rejected`, which leaves it open), `answeredBy` (`user` or `agent`), `reason` |
| Progress from a subagent, a worker, a remote agent, or a run that timed out | `ACTIVITY_SNAPSHOT`, one activity per source (`messageId` is `notify-progress-<task id>`, or `notify-progress-<agent name>`), each snapshot replacing the last; `TOOL_CALL_*` for tool calls |
| A subagent finished and handed in its result | `ACTIVITY_SNAPSHOT` for its activity with `content.done: true`, then its result as a text message with `subagentRunId` and `rockbot.origin`, as above. The agent's own answer about the result follows later as a separate message. |
| A subagent or worker started or finished | `SUBAGENT_STARTED`, then `SUBAGENT_FINISHED` or `SUBAGENT_ERROR` |
| A subagent's task list changed | `STATE_SNAPSHOT`: `{"taskLists": {"<task id>": [...]}}` with every list of the thread's running background work. Always a whole snapshot, never `STATE_DELTA`. A finished subagent's list is removed. |
| The agent is still working on a run that ended with `rockbot.reply_pending` | `CUSTOM rockbot.agent_run_started`, `rockbot.heartbeat` and `rockbot.run_finished` (`outcome`, `error`), each with the run's `runId` |
| An MCP server needs a sign-in, or one finished | `CUSTOM rockbot.mcp_signin_status`: `serverName`, `state`, `signInId`, `message` |
| The agent's display name changed | `CUSTOM rockbot.agent_name`: `agentName` |
| The agent (re)started and reports its running background work | `CUSTOM rockbot.active_work`: `work`, the thread's running subagents and workers. Empty after a restart: clear any indicator still showing. |
| The proxy has an error to show outside a run | `CUSTOM rockbot.error`: `message` |
| The stream itself | `CUSTOM rockbot.connected` and `CUSTOM rockbot.resync`, see below |

The task-list state on this stream is the stream's own document. Keep it apart from the state a run
sends: each describes different lists, and a snapshot from one doesn't replace the other's.

**Which thread gets what.** An event goes to the stream of the thread (session) it belongs to. Three
kinds of session belong to no one conversation and go to every thread, as the Blazor UI shows them
to everyone: `scheduled` (your scheduled tasks), `scheduled-system` (system jobs) and `a2a-inbound`
(notifications from other agents). So does everything that has no session: the agent's name,
sign-in statuses, active work and errors. Traffic for other clients' sessions, such as
`blazor-session`, is not sent.

A run's own events are only in the run's stream, never here as well.

#### Reconnecting

Every event has an `id`. Treat it as an opaque string. When the connection drops, reconnect and
send the last id you received in the `Last-Event-ID` header, which a browser's `EventSource` does
by itself. A client that can't set headers can send it as the `lastEventId` query parameter instead.

```bash
curl -N 'http://rockbot-agui/agui/notify?threadId=agui-session' -H 'Last-Event-ID: 5f0c2a9e-3'
```

The proxy keeps the latest events of each allowed thread (200 events, for up to 24 hours), whether or
not anyone is connected. What a connection starts with depends on the id:

| You send | The stream starts with |
|---|---|
| No id | `CUSTOM rockbot.connected`. Its `id` is the stream's position now, so you have an id to reconnect with before anything else arrives. Fetch `GET /threads/{id}/messages` **after** this event to load what happened earlier. |
| An id the buffer still reaches | Every event after it, in order, then live events. Nothing is skipped and nothing is repeated. |
| An id older than the buffer | `CUSTOM rockbot.resync` with `reason: "expired"`, then live events. |
| An id this proxy didn't issue | `CUSTOM rockbot.resync` with `reason: "unknown"`, then live events. |

On `rockbot.resync` you have missed events that can't be replayed: refetch
`GET /threads/{id}/messages`, clear any progress indicators, and carry on from the resync event's
`id`.

The part of an id before the dash identifies the proxy process. It changes when the proxy restarts,
and each replica has its own. An id from before a restart, or from another replica, is "unknown"
even if its number looks valid, so it is never mistaken for a position in the new sequence. The
buffer is in memory and per replica, which is why the chart runs one replica.

What history can't give back after a resync:

- **Origins.** History has each message's text, time, agent name and attachments, but not the
  `rockbot.origin` detail (which channel and prompt started the work).
- **Scheduled-task and inbound A2A output.** It was never part of the thread's conversation, so it
  isn't in the thread's history.
- **Progress and status events.** They describe a moment that has passed.

A client that reads too slowly is disconnected rather than allowed to hold up the others: once 256
events are waiting for it, the proxy ends its stream (the connection may be reset rather than closed
cleanly). Reconnect with the last id you processed, as after any other drop.

### MCP questions

An MCP server can stop in the middle of a tool call and ask the user something: which calendar,
whether to really delete, a missing field. The agent hands the question to the client as a form (an
[`McpQuestionForm`](https://github.com/MarimerLLC/rockbot/blob/main/src/RockBot.UserProxy.Abstractions/McpQuestionForm.cs):
a message, fields, an expiry), the user answers or declines, and the tool call goes on. The proxy
offers this two ways, and a client picks one per request.

#### Getting forms at all

The agent only sends forms to a session that says it can show them, with the `InteractiveForms`
capability. The default capabilities leave it out, so **a client that says nothing gets no forms**:
the agent asks the question in its reply as ordinary text and the user answers in their next
message. Nothing on this page then applies, and such a client is never blocked.

A request asks for forms in either of two ways:

- `forwardedProps.rockbot.questionMode` is set. Naming a mode declares `InteractiveForms`, added to
  whatever capabilities the request has (the defaults, or the ones it lists).
- `forwardedProps.rockbot.capabilities` lists `InteractiveForms`. Without a `questionMode` the mode
  is `interrupt`.

| `questionMode` | `InteractiveForms` listed | The run works in |
|---|---|---|
| not set | no | no mode: questions come as text |
| not set | yes | `interrupt` |
| `"interrupt"` | either | `interrupt` |
| `"custom"` | either | `custom` |

Any other value of `questionMode` is refused with `400`.

The agent remembers the capabilities of the **last message** on a thread. If two clients share a
thread and only one asks for forms, whichever sent the latest message decides how the next question
is asked.

#### `interrupt` mode: AG-UI's own way

This is the AG-UI 1.0 interrupt protocol, so a generic AG-UI client that implements interrupts
needs nothing RockBot-specific beyond asking for the mode.

**A question during a run** ends the run with an interrupt outcome. The reply (usually the agent
saying a form is waiting) comes first:

```bash
curl -N -X POST http://rockbot-agui/agui -H 'Content-Type: application/json' -d '{
  "threadId": "agui-session", "runId": "run-0002",
  "messages": [ { "id": "m1", "role": "user", "content": "Set up the review meeting" } ],
  "forwardedProps": { "rockbot": { "questionMode": "interrupt" } }
}'
```

```
data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-0002",…}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"7c1e…","delta":"The scheduler needs a time zone; please fill in the form.",…}

data: {"type":"RUN_FINISHED","threadId":"agui-session","runId":"run-0002","outcome":{"type":"interrupt","interrupts":[
  {"id":"q-3f9a","reason":"input_required","message":"Which time zone?","expiresAt":"2026-11-14T10:30:00.000Z",
   "responseSchema":{"type":"object","properties":{"tz":{"type":"string","title":"Time zone"}},"required":["tz"]},
   "metadata":{"rockbot":{"serverName":"scheduler","toolName":"create_event","form":{…}}}}]},…}
```

Each open question is one interrupt:

| Field | Value |
|---|---|
| `id` | The question's id. A `resume` entry names it. |
| `reason` | `confirmation` when the form is a single yes/no decision (show two buttons), otherwise `input_required`. |
| `message` | The server's question. It is untrusted text from the MCP server: show it as text, never as markup. |
| `expiresAt` | When the question can no longer be answered, ISO 8601 in UTC. |
| `responseSchema` | A JSON Schema for the answer, see [below](#the-answer-schema). |
| `metadata.rockbot` | `serverName`, `toolName` and `form`, the whole `McpQuestionForm`. |

**Resuming.** Send a new run on the thread whose `resume` answers the interrupts. `resolved`
carries the answer as `payload`, an object of field values keyed by field name; `cancelled`
declines the question (the server carries on without the values):

```bash
curl -N -X POST http://rockbot-agui/agui -H 'Content-Type: application/json' -d '{
  "threadId": "agui-session", "runId": "run-0003", "messages": [],
  "resume": [ { "interruptId": "q-3f9a", "status": "resolved", "payload": { "tz": "Europe/Brussels" } } ]
}'
```

```
data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-0003",…}

data: {"type":"CUSTOM","name":"rockbot.mcp_question_status","value":{"sessionId":"agui-session","questionId":"q-3f9a","state":"answered","answeredBy":"user"},…}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"b02d…","delta":"The review is booked for 10:00 Brussels time.",…}

data: {"type":"CUSTOM","name":"rockbot.origin","value":{"messageId":"b02d…","source":{…},"followsQuestionId":"q-3f9a"},…}

data: {"type":"RUN_FINISHED","threadId":"agui-session","runId":"run-0003",…}
```

- A run with `resume` only answers interrupts. Its `messages` are ignored (a client replays the
  conversation with every request), and it needs no `questionMode`.
- **`resume` must answer every open interrupt** on the thread. One that leaves any out answers
  none: the run ends with `RUN_ERROR` and code `resume_incomplete`.
- The run streams each question's status as `CUSTOM rockbot.mcp_question_status`, then the agent's
  reply to each accepted answer as a text message followed by `CUSTOM rockbot.origin` with
  `followsQuestionId`. It ends when every answered question is settled and each reply has arrived.
- **A refused answer** (it failed the server's schema, say) has the state `rejected`. The question
  is still open, and the run ends with `RUN_ERROR`, code `interrupt_rejected` and the reason as its
  message. Resume it again with a corrected answer. Other answers in the same `resume` that were
  accepted stay accepted; their replies arrive on the notify stream.
- A question that turns out to be closed already (`unavailable`, `expired`, `cancelled`,
  `interrupted`) is reported by its status event and then counts as settled: the run ends normally.
- If the server asks **another question** after the answer, the resume run ends with that one as a
  new interrupt.
- If the agent never acknowledges an answer within `UserProxy:ReplyTimeout`, the run ends with
  `RUN_ERROR` and code `agent_lost`; the interrupt is still open.
- The agent's reply can take a while (the tool call continues, then the model answers), and some
  answers get none (one that goes on to a background task). After `AgUi:ResumeReplyTimeout` the run
  ends with `CUSTOM rockbot.reply_pending` and `RUN_FINISHED`, like any long turn, and the reply
  arrives on the notify stream if it comes.
- **Aborting a resume run cancels nothing.** The answer is already sent. The agent's reply then
  goes to the notify stream. (Aborting a run with new input does cancel, see
  [Cancelling](#cancelling).)
- Two runs can't resume the same interrupt at once; the second gets `409`.

**New input is blocked** while the thread has open questions, as AG-UI requires. A run in
`interrupt` mode with a message and no `resume` doesn't reach the agent. It is a stream, not an
HTTP error, because this is a state the client is expected to handle and show:

```
data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-0004",…}

data: {"type":"CUSTOM","name":"rockbot.mcp_question","value":{"sessionId":"agui-session","form":{…},"text":"…","interrupt":{"id":"q-3f9a",…}},…}

data: {"type":"RUN_ERROR","code":"interrupts_pending","message":"This thread has interrupts to answer before it takes new input. Send a run whose resume answers q-3f9a.",…}
```

The open questions come before the error (here, and before `resume_incomplete`), each with its
interrupt, so a client that lost them, after a reload for example, can still answer. Only
`interrupt`-mode requests are blocked: a `custom`-mode request, or one that didn't ask for forms,
always goes through.

**A question outside a run.** A question can arrive when no run is open (a subagent or a
background task asked). It then goes to the [notify stream](#the-notify-stream-get-aguinotify) as
`CUSTOM rockbot.mcp_question`, whose `interrupt` property is the same interrupt object a
`RUN_FINISHED` would carry. The next run on the thread must resume it.

A question held for a run that then can't carry it, because the run failed, was cancelled or its
client went away, is put on the notify stream instead.

#### `custom` mode: RockBot's own way

This is how the Blazor UI works, for a client written for RockBot. Questions are events, they are
answered over REST, and the user can keep chatting while one is open.

- A question is a `CUSTOM rockbot.mcp_question` event: **in the run** when one is open on the
  thread, otherwise on the notify stream. Never both. Its value has `form`, `text` (the same
  question as plain text), `sessionId`, `agentName` and `interrupt`.
- The run ends with a plain `RUN_FINISHED`, and new input is never blocked.
- Answer with [`POST /questions/{id}/answer`](#post-questionsidanswer).
- What becomes of the answer arrives on the notify stream: `CUSTOM rockbot.mcp_question_status`
  (`answered`, `declined`, or `rejected` with a `reason`, in which case the form can be submitted
  again), then the agent's reply as a text message with `rockbot.origin` and `followsQuestionId`.

```
data: {"type":"RUN_STARTED","threadId":"agui-session","runId":"run-0002",…}

data: {"type":"CUSTOM","name":"rockbot.mcp_question","value":{"sessionId":"agui-session","agentName":"mcp-bridge",
  "form":{"questionId":"q-3f9a","serverName":"scheduler","toolName":"create_event","message":"Which time zone?",
          "expiresAt":"2026-11-14T10:30:00+00:00","fields":[{"name":"tz","kind":"string","title":"Time zone","required":true}]},
  "text":"scheduler asks: Which time zone? …","interrupt":{"id":"q-3f9a",…}},…}

data: {"type":"TEXT_MESSAGE_CONTENT","messageId":"7c1e…","delta":"The scheduler needs a time zone; please fill in the form.",…}

data: {"type":"RUN_FINISHED","threadId":"agui-session","runId":"run-0002",…}
```

##### `POST /questions/{id}/answer`

```bash
curl -X POST http://rockbot-agui/questions/q-3f9a/answer -H 'Content-Type: application/json' \
  -d '{ "threadId": "agui-session", "answers": { "tz": "Europe/Brussels" } }'
# or, to decline:
#  -d '{ "threadId": "agui-session", "decline": true }'
```

```json
{ "questionId": "q-3f9a", "threadId": "agui-session", "known": true }
```

| Body property | Use |
|---|---|
| `threadId` | The thread the question is on. Optional when the proxy knows the question, or allows only one thread. |
| `answers` | An object of field values keyed by field name, as the form's `fields` (or the interrupt's `responseSchema`) describe. Required unless `decline` is `true`. |
| `decline` | `true` to refuse the question. `answers` is then ignored. |

The answer is `202`: the proxy has passed it to the agent, and the outcome arrives on the notify
stream as described above. `known` says whether the proxy had the question as open. **An unknown
question is passed on too**, because the agent is the one that knows (see
[What the proxy remembers](#what-the-proxy-remembers)); the agent reports `unavailable` if there is
no such question.

| Status | Why |
|---|---|
| `400` | The body isn't a JSON object, has neither `answers` (an object) nor `decline: true`, or needs a `threadId`. |
| `403` | The thread is not allowed. |
| `404` | The proxy knows the question, and it is on another thread. |
| `503` | The proxy is not connected to the message bus. |

This endpoint works whatever mode the question arrived in. A client in `interrupt` mode normally
has no use for it, since a run with `resume` does the same and streams the result.

#### Which mode applies when

The mode belongs to a request, not to the thread, and questions arrive when the agent sends them,
which may be when no request is open. The rules:

1. **A question that arrives while a run is open on its thread** is handled by that run, in that
   run's mode: shown as an event (`custom`) or held for `RUN_FINISHED` (`interrupt`). A run that
   didn't ask for forms doesn't count as open for this.
2. **A question that arrives with no such run open** goes to the notify stream. The event is the
   same in both modes and has both the form and the interrupt.
3. **The next request's own mode decides whether it is blocked.** `interrupt`: blocked until the
   open questions are resumed. `custom`, or no forms: never blocked.
4. **Either way of answering works for any question**: a run with `resume`, or the answer endpoint.
5. A reply to an answer goes to the run that resumed the question, if that run is still waiting for
   it. Otherwise it goes to the notify stream. It is never in both.
6. A question's status events are always on the notify stream (every client showing the form has to
   close it), and also in the run that resumed it.

Runs with different modes can share a thread. If two runs are open on a thread when a question
arrives, each handles it in its own mode.

#### The answer schema

`responseSchema` is rebuilt from the form's fields: an object with one property per field, named as
the server named it, and `required` listing the required ones. The MCP server's own schema is the
one the agent enforces; this copy is there so a client can build a form and check an answer before
sending it.

| Field `kind` | Schema |
|---|---|
| `string` | `{"type":"string"}`, with `minLength`, `maxLength` and `format` (`email`, `uri`, `date`, `date-time`) when the server gave them |
| `number`, `integer` | `{"type":"number"}` or `{"type":"integer"}`, with `minimum` and `maximum` |
| `boolean` | `{"type":"boolean"}` |
| `single-select` | `{"type":"string","enum":[values]}`, plus `"oneOf":[{"const":value,"title":label}]` when a choice's label differs from its value |
| `multi-select` | `{"type":"array","uniqueItems":true,"items":{"type":"string","enum":[values]}}`, with `minItems` and `maxItems`, and `anyOf` on `items` for labels |
| anything else | no constraint (a kind from a newer agent) |

Every property also gets `title`, `description` and `default` when the field has them. A
`confirmation` interrupt has the same kind of schema as any other: an object with the one boolean
property, for example `{"approve": true}`.

#### What the proxy remembers

The agent owns a question. The proxy only keeps a list, per thread, of the questions it has seen
arrive as forms and not yet heard settled: a form opens one; a status of `answered`, `declined`,
`expired`, `cancelled`, `interrupted` or `unavailable` closes it; `rejected` leaves it open; and it
drops out by itself at its `expiresAt`. That list is what blocks new input and what a `resume` has
to cover.

The list is in memory, so **after a proxy restart it is empty**, though the questions may still be
open on the agent. The proxy then errs on the side of letting things through:

- New input is not blocked, even in `interrupt` mode.
- A `resume` entry, or a `POST /questions/{id}/answer`, for a question the proxy doesn't know is
  passed to the agent, which answers with a status: `answered` if the question was still open,
  `unavailable` if not.
- A client that still shows the form from before the restart can therefore still answer it.

With more than one replica each has its own list, built from the same broadcasts, so they agree
except for what one of them missed while it was starting.

Each replica also decides by itself where a reply to an answer goes. With more than one, the
replica that runs a `resume` streams the reply in that run, and the others, which have no run
waiting, put it on their notify streams as well.

### Cancelling

There are two ways to cancel, and both stop **all** of the thread's in-flight work on the agent:

- **Abort the request** (close the connection). This is the AG-UI convention. The proxy sends the
  agent a cancel request for the thread.
- **`POST /threads/{id}/cancel`** from another connection. The open run then ends with
  `RUN_FINISHED` and `outcome.type: "cancelled"` once the agent reports it.

A client that loses its connection therefore cancels its turn. A phone that drops off the network
mid-run should send the message again.

The exception is a run with `resume`: aborting it cancels nothing, because the answer it carried is
already with the agent. See [MCP questions](#mcp-questions).

### History: `GET /threads/{id}/messages`

Returns the thread's history as one AG-UI `MESSAGES_SNAPSHOT` event:

```json
{
  "type": "MESSAGES_SNAPSHOT",
  "timestamp": 1794547308650,
  "messages": [
    { "id": "agui-session-0", "role": "user", "content": "Chart last week's sales",
      "metadata": { "rockbot": { "timestamp": "2026-11-14T09:30:00+00:00" } } },
    { "id": "agui-session-1", "role": "assistant", "name": "RockBot", "content": "Here it is.",
      "metadata": { "rockbot": { "timestamp": "2026-11-14T09:30:05+00:00", "agentName": "RockBot",
        "attachments": [ { "mime": "image/png", "path": "chart.png" } ] } } }
  ]
}
```

A message's `id` is its position in the thread (`{threadId}-{index}`), which changes when the
context is cleared. It is not the `messageId` the run's stream used for the same reply. RockBot's
own detail is under `metadata.rockbot`. Attachments are listed by path; the proxy can't serve their
bytes yet.

`403` for a thread that isn't allowed, and `504` when the agent doesn't answer in time.

## Configuration

Standard .NET configuration: `appsettings.json`, environment variables (`AgUi__DefaultUserId`), user
secrets.

| Key | Default | Use |
|---|---|---|
| `RabbitMq:HostName`, `Port`, `UserName`, `Password`, `VirtualHost` | | The broker, as for every RockBot process |
| `Agent:Name` | `RockBot` | The agent to talk to |
| `UserProxy:ProxyId` | `agui-<machine name>` | This proxy's identity on the bus. It must be unique per replica; in Kubernetes the machine name is the pod name. |
| `UserProxy:ReplyTimeout` | `00:03:00` | How long a run waits for the agent to start it, before it ends with `rockbot.reply_pending`. Also the wait for a history request, and for the agent to acknowledge an answer sent with `resume`. |
| `UserProxy:MaxTurnDuration` | `00:30:00` | The longest a run may last while the agent keeps sending heartbeats. After that it ends with `rockbot.reply_pending`. |
| `AgUi:AllowedThreadIds` | `["agui-session"]` | The thread ids clients may use. Others get `403`. Setting any replaces the default. |
| `AgUi:DefaultUserId` | `agui-user` | The user id sent to the agent for every caller |
| `AgUi:KeepAliveInterval` | `00:00:15` | How often an idle run or notify stream gets an SSE comment. Zero or less turns it off. |
| `AgUi:ResumeReplyTimeout` | `00:05:00` | How long a run with `resume` waits for the agent's reply after the agent accepted an answer, before it ends with `rockbot.reply_pending`. |
| `AgUi:NotifyBufferSize` | `200` | How many notify events are kept for each thread, for replay after a reconnect. Zero keeps none. |
| `AgUi:NotifyBufferAge` | `1.00:00:00` | How long a notify event is kept for replay. Zero or less sets no age limit. |
| `AgUi:NotifySubscriberQueueSize` | `256` | How many notify events may wait for one connected client before it is disconnected. Never less than `NotifyBufferSize` + 1. |

Every thread is a separate session on the agent, with its own history and working memory, so the
list of allowed thread ids is short on purpose. By default all clients share `agui-session`, the
way every browser shares `blazor-session` in the Blazor UI.

To run it locally against a broker:

```bash
RabbitMq__HostName=localhost RabbitMq__UserName=… RabbitMq__Password=… \
  dotnet run --project src/RockBot.UserProxy.AgUi
# listens on http://localhost:5140
```

## Deployment

The image is `rockylhotka/rockbot-agui`, built from the repository root:

```bash
docker build -f src/RockBot.UserProxy.AgUi/Dockerfile -t rockylhotka/rockbot-agui:latest .
```

The Helm chart deploys it when `agui.enabled` is `true` (it is off by default):

```yaml
agui:
  enabled: true
  allowedThreadIds: []          # empty allows only "agui-session"
  tailscale:
    hostname: rockbot-agui      # http://rockbot-agui on your tailnet
    ingress:
      enabled: false            # true: https://rockbot-agui.<tailnet>.ts.net
```

| Value | Default | Use |
|---|---|---|
| `agui.enabled` | `false` | Deploy the proxy |
| `agui.image.repository`, `agui.image.tag` | `rockylhotka/rockbot-agui`, `latest` | The image |
| `agui.replicaCount` | `1` | Replicas. Each has its own identity on the bus, so a run works on any of them. Each also has its own notify buffer, so with more than one a reconnect that lands on another replica is told to resync. |
| `agui.replyTimeout` | `""` | `UserProxy:ReplyTimeout`; empty keeps 3 minutes |
| `agui.allowedThreadIds` | `[]` | `AgUi:AllowedThreadIds` |
| `agui.tailscale.hostname`, `.tags`, `.ingress.enabled`, `.ingress.proxyGroup` | `rockbot-agui`, … | Tailnet exposure, as for `blazor.tailscale` |
| `agui.ingress.enabled`, `.className`, `.host`, `.annotations`, `.tls` | off | An ordinary HTTPS ingress |
| `agui.ingress.allowUnauthenticated` | `false` | Required to be `true` for that ingress to render |

By default the Service is a Tailscale LoadBalancer, private to the tailnet. Because the proxy has no
sign-in, the chart refuses to render the ordinary HTTPS ingress unless
`agui.ingress.allowUnauthenticated` is `true`, which states that something outside the chart
authenticates requests. An ingress in front of the proxy must not buffer responses and needs a read
timeout longer than a run; `values.yaml` shows the nginx annotations. The notify stream stays open
indefinitely, with a comment every 15 seconds, so an idle timeout shorter than that would cut it.

The chart has no values for the notify buffer or the resume timeout. To change them, set the
`AgUi__NotifyBufferSize`, `AgUi__NotifyBufferAge`, `AgUi__NotifySubscriberQueueSize` or
`AgUi__ResumeReplyTimeout` environment variables on the deployment.

The pod needs no volume. The notify buffer is in memory, so it is empty after a restart and
clients are told to resync. Its queues on the broker are ephemeral: they are deleted when the pod
stops, and the broker expires them if it crashes.

## Using a client

The .NET SDK's `AGUIChatClient` works against the proxy, and is what the conformance tests use:

```csharp
var chat = new AGUIChatClient(new AGUIChatClientOptions(httpClient, "http://rockbot-agui/agui"));
var response = await chat.GetResponseAsync(
    [new ChatMessage(ChatRole.User, "Hello")],
    new ChatOptions { ConversationId = "agui-session" });   // the AG-UI threadId
```

Set the conversation id to an allowed thread id. A client that makes up its own thread id gets `403`.

The client implements AG-UI interrupts, so MCP questions work with it as they are. An interrupt
arrives as `InterruptRequestContent` in the response; answer it with `InterruptResponseContent` in
the next call, which the client sends as `resume`:

```csharp
var options = new ChatOptions
{
    ConversationId = "agui-session",
    // forwardedProps.rockbot.questionMode: ask for questions as interrupts.
    RawRepresentationFactory = _ => new RunAgentInput
    {
        ForwardedProperties = JsonSerializer.SerializeToElement(new { rockbot = new { questionMode = "interrupt" } }),
    },
};

var response = await chat.GetResponseAsync(messages, options);
messages.AddRange(response.Messages);

foreach (var question in response.Messages.SelectMany(m => m.Contents).OfType<InterruptRequestContent>())
{
    // question.Message, question.Reason, question.ResponseSchema, question.ExpiresAt
    var answer = JsonSerializer.SerializeToElement(new { tz = "Europe/Brussels" });
    messages.Add(new ChatMessage(ChatRole.User, [new InterruptResponseContent(question.RequestId) { Payload = answer }]));
}

response = await chat.GetResponseAsync(messages, options);   // the run that resumes
```

`AGUIChatClient` only runs turns. For the notify stream use any server-sent events client, for
example `System.Net.ServerSentEvents.SseParser` in .NET or `EventSource` in a browser, and read
each event's `data` as an AG-UI event with the SDK's `AGUIJsonSerializerContext.Default.BaseEvent`.
