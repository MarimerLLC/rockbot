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
RockBot's own events), cancelling, history, the plain REST endpoints for everything else a
RockBot client does (attachments, feedback, saved responses, MCP sign-in, clearing the context,
agent info), and who may call it: [app auth](#app-auth) with bearer tokens, and [CORS](#cors) for
browser apps. A small Blazor WebAssembly app in the repository shows a client using all of it; see
[A reference client](#a-reference-client). The full design is in
[`design/userproxy-agui.md`](https://github.com/MarimerLLC/rockbot/blob/main/design/userproxy-agui.md).

> **By default the proxy asks nobody who they are** ("network mode"). Whoever can reach it can do
> everything on this page: talk to the agent as its user, read the thread's history, the saved
> responses and every file in the attachments directory, upload files, and start MCP sign-ins.
> Run it on a private network such as a tailnet, or turn on [app auth](#app-auth), which is what
> an API reachable from the internet needs. A browser app on another origin can't call it until
> you list its [origin](#cors): the proxy sends no CORS headers for other origins and
> [refuses their requests](#the-origin-check).

## Endpoints

| Endpoint | Does |
|---|---|
| `POST /agui` | Runs one turn. The body is an AG-UI `RunAgentInput`; the response is the run's event stream. |
| `GET /agui/notify?threadId=` | The thread's unsolicited output, as a stream of AG-UI events that stays open. |
| `POST /questions/{id}/answer` | Answers or declines an MCP question. Answers `202`. See [MCP questions](#mcp-questions). |
| `GET /threads/{id}/questions` | The MCP questions open on the thread. See [Open questions](#open-questions-get-threadsidquestions). |
| `POST /threads/{id}/cancel` | Asks the agent to stop the thread's work. Answers `202`. |
| `POST /threads/{id}/clear` | Clears the thread's conversation context. Answers `202`. See [Clearing the context](#clearing-the-context-post-threadsidclear). |
| `GET /threads/{id}/messages` | The thread's conversation history, as one `MESSAGES_SNAPSHOT` event in JSON. |
| `POST /attachments` | Uploads a file to send with a message. See [Attachments](#attachments). |
| `GET /attachments/{path}` | The bytes of an attachment. |
| `POST /feedback` | Thumbs up or down on a reply. See [Feedback](#feedback-post-feedback). |
| `GET /saved`, `GET /saved/{id}`, `POST /saved`, `DELETE /saved/{id}` | The saved responses. See [Saved responses](#saved-responses). |
| `GET /mcp-signin`, `POST /mcp-signin/{server}/start` | Signing in to an OAuth-protected MCP server. See [MCP sign-in](#mcp-sign-in). |
| `GET /mcp-oauth/callback` | Where an authorization server sends the browser back. Not called by a client. |
| `GET /agent` | The agent's name, version and current work. See [Agent info](#agent-info-get-agent). |
| `GET /agent/introspection` | What the agent is configured with and what it has used: its models, its pricing table, its token usage and cost. See [Introspection](#introspection-get-agentintrospection). |
| `POST /tickets` | A short-lived URL for an attachment or the notify stream, for a browser request that can't send a token. See [Access tickets](#access-tickets-post-tickets). |
| `GET /healthz` | `200` while the proxy is subscribed on the message bus, `503` otherwise. |

The REST endpoints answer in JSON with camelCase names, the same shapes the events use. A refusal
is a problem-details body (`application/problem+json`) whose `detail` says what was wrong. Three
statuses mean the same everywhere: `403` for a thread that isn't allowed (and for a browser
request from an [origin that isn't allowed](#the-origin-check)), `503` when the proxy is
not connected to the message bus, and `504` when the agent didn't answer within
`AgUi:RequestTimeout`. An endpoint that takes a `threadId` lets you leave it out when the proxy
allows only one thread.

With [app auth](#app-auth) on, every endpoint except `GET /healthz` and `GET /mcp-oauth/callback`
also answers `401` to a request without a valid token and `403` to an account that isn't allowed.
The examples on this page leave the `Authorization` header out.

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
  parts of a message are dropped; send files as attachments, below.
- **`forwardedProps.rockbot.attachments`** (optional) lists the files that go with the message, each
  as [`POST /attachments`](#attachments) returned it. A message may be attachments with no text.
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
- A reply's attachments are listed in `CUSTOM rockbot.attachments`. Each has a `url` to fetch its
  bytes from; see [Attachments](#attachments).
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
| `400` | The body isn't a `RunAgentInput`, has no `threadId` or `runId`, has no user message with text (and no attachments and no `resume`), names an unknown `questionMode`, has an attachment reference the proxy doesn't accept (see [Attachments](#attachments)), or has a malformed `resume` entry: no `interruptId`, a `status` other than `resolved` or `cancelled`, a `resolved` entry whose `payload` isn't an object, or two entries for one interrupt. |
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
| A reply nobody is waiting for: a scheduled task's output, the agent's answer after a subagent finished, an A2A result, a late reply, the reply to a form answer that no run is waiting for | `TEXT_MESSAGE_START` / `CONTENT` / `END`, `CUSTOM rockbot.attachments` when it has any (each with its `url`), then `CUSTOM rockbot.origin` with `messageId`, `sessionId` (the session the reply was sent in: the thread's own, or `scheduled`, `scheduled-system` or `a2a-inbound`, see below), `origin` (`channel`, `promptSummary`, `startedAt`, `sessionId`: where the work that led to it was started), `source`, and `followsQuestionId` on the reply to a form answer |
| An MCP server's question, as a form, when no run on the thread took it | `CUSTOM rockbot.mcp_question`: `form` (the [`McpQuestionForm`](https://github.com/MarimerLLC/rockbot/blob/main/src/RockBot.UserProxy.Abstractions/McpQuestionForm.cs)), `text` (the same question as plain text), `interrupt` (the question as an AG-UI interrupt), `sessionId`, `agentName`. Only a thread whose last message asked for forms gets them. See [MCP questions](#mcp-questions). |
| A question changed state | `CUSTOM rockbot.mcp_question_status`: `sessionId`, `questionId`, `state` (`answered`, `declined`, `expired`, `cancelled`, `interrupted`, `unavailable`, or `rejected`, which leaves it open), `answeredBy` (`user` or `agent`), `reason` |
| Progress from a subagent, a worker, a remote agent, or a run that timed out | `ACTIVITY_SNAPSHOT`, one activity per source (`messageId` is `notify-progress-<task id>`, or `notify-progress-<agent name>`), each snapshot replacing the last; `TOOL_CALL_*` for tool calls |
| A subagent finished and handed in its result | `ACTIVITY_SNAPSHOT` for its activity with `content.done: true`, then its result as a text message with `subagentRunId` and `rockbot.origin`, as above. The agent's own answer about the result follows later as a separate message. |
| A subagent or worker started or finished | `SUBAGENT_STARTED`, then `SUBAGENT_FINISHED` or `SUBAGENT_ERROR` |
| A subagent's task list changed | `STATE_SNAPSHOT`: `{"taskLists": {"<task id>": [...]}}` with every list of the thread's running background work. Always a whole snapshot, never `STATE_DELTA`. A finished subagent's list is removed. |
| The agent is still working on a run that ended with `rockbot.reply_pending` | `CUSTOM rockbot.agent_run_started`, `rockbot.heartbeat` and `rockbot.run_finished` (`outcome`, `error`), each with the run's `runId` |
| An MCP server needs a sign-in, or one finished | `CUSTOM rockbot.mcp_signin_status`: `serverName`, `state`, `signInId`, `message`. See [MCP sign-in](#mcp-sign-in). |
| Someone cleared the thread's context | `CUSTOM rockbot.context_cleared`: `threadId`. Drop what you show of the conversation. See [Clearing the context](#clearing-the-context-post-threadsidclear). |
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

**Replying to what the stream showed.** A scheduled task's or inbound A2A notification's output is
stored in its own session, not the thread's. When the thread next sends a message, the proxy
attaches references to those replies the thread was shown since its last message (at most five, by
`messageId`), and the agent copies them into the thread's session before answering, so "summarize
that" has them as context. Nothing to do on the client. Each thread keeps its own place, so two
threads that saw the same report each get it once.

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

#### Open questions: `GET /threads/{id}/questions`

A client that starts fresh, or reconnects to the notify stream too late to replay the form, asks
for the thread's open questions:

```bash
curl http://rockbot-agui/threads/agui-session/questions
```

```json
{
  "threadId": "agui-session",
  "questions": [
    { "sessionId": "agui-session", "agentName": "mcp-bridge",
      "form": { "questionId": "q-3f9a", "serverName": "scheduler", "toolName": "create_event",
                "message": "Which time zone?", "expiresAt": "2026-11-14T10:30:00+00:00",
                "fields": [ { "name": "tz", "kind": "string", "title": "Time zone", "required": true } ] },
      "text": "scheduler asks: Which time zone? …",
      "interrupt": { "id": "q-3f9a", "reason": "input_required", "message": "Which time zone?", "…": "…" } }
  ]
}
```

Each entry is the `value` of the `CUSTOM rockbot.mcp_question` event the question arrived in,
oldest first, so one piece of code reads both. Answer one with a run that has `resume`, or with
`POST /questions/{id}/answer`. The list is the proxy's own (see above): empty after a proxy
restart even if the agent still has questions open. Load it after the notify stream's
`rockbot.connected` event, as with history, so nothing falls between the two.

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
    { "id": "5f0c…", "role": "user", "content": "Chart last week's sales",
      "metadata": { "rockbot": { "timestamp": "2026-11-14T09:30:00+00:00", "sessionId": "agui-session" } } },
    { "id": "9a41…", "role": "assistant", "name": "RockBot", "content": "Here it is.",
      "metadata": { "rockbot": { "timestamp": "2026-11-14T09:30:05+00:00", "agentName": "RockBot",
        "sessionId": "agui-session", "source": { "kind": "primary" },
        "attachments": [ { "mime": "image/png", "path": "chart.png" } ] } } },
    { "id": "c27e…", "role": "assistant", "name": "RockBot", "content": "**Daily summary.** …",
      "metadata": { "rockbot": { "timestamp": "2026-11-14T10:00:02+00:00", "agentName": "RockBot",
        "sessionId": "scheduled", "source": { "kind": "scheduled" },
        "origin": { "channel": "scheduled", "promptSummary": "daily-summary",
          "startedAt": "2026-11-14T10:00:00+00:00", "sessionId": "scheduled" } } } }
  ]
}
```

A message's `id` is the stored turn's own id, and a reply the agent stored has the same id as the
`messageId` it had on a stream. A client that loads history while replies are still arriving can
use it to show each message once. Turns from an agent too old to store ids have
`{threadId}-{index}` instead, their position in the thread, which changes when the context is
cleared.

RockBot's own detail is under `metadata.rockbot`:

- `timestamp` and `agentName`.
- `attachments`. Each attachment also has the `url` of its bytes (`"url": "/attachments/chart.png"`),
  as in `rockbot.attachments`.
- `sessionId`: the thread, or the broadcast session the message went out in (`scheduled`,
  `scheduled-system`, `a2a-inbound`). The thread's notify stream carries those sessions, so its
  history includes them too, merged in by time.
- `origin` and `source`, as the reply's `rockbot.origin` event had them live. Render the same
  anchor from `origin`. Both are absent for the user's own turns and from older agents.
- `importedFromSessionId`: on a copy of another session's reply, made when the user replied to it
  from this thread, the session the original was in. Render the anchor as for a reply from that
  session (when there is no `origin`, name it). Absent on every other message.

Two kinds of message are not what the user typed or the agent answered:

- **`"role": "system"`** is a prompt the agent wrote to itself, such as the note it works from
  after a subagent finishes. Leave these out of what you show.
- **A `user` message with a `metadata.rockbot.source`** (`subagent`, `worker`, `a2a`, `bridge`) is
  a result the agent stored as input to itself. Show it as that source's output, not as the user's
  words. From an older agent, which records no source, tell them by `agentName`: `subagent-<task
  id>` or `worker-<task id>` for a subagent's or worker's output, another agent's name for an A2A
  result.

`403` for a thread that isn't allowed, and `504` when the agent doesn't answer in time.

### Attachments

Files never travel inside a message. The agent keeps them on a volume it shares with the proxy, and
messages carry a reference: `mime`, `path` (relative to the attachments directory) and optionally
`fileName` (the name to show). The proxy turns that into two HTTP calls, one each way.

**Sending a file** takes two steps: upload it, then name it on the run.

```bash
curl -X POST 'http://rockbot-agui/attachments?threadId=agui-session' -F 'file=@screenshot.png;type=image/png'
```

```json
{ "mime": "image/png", "path": "screenshot-1f3a.png", "fileName": "screenshot.png",
  "url": "/attachments/screenshot-1f3a.png" }
```

The answer is `201`. Put the object, as it is, in the run's `forwardedProps.rockbot.attachments`
(`url` is ignored there):

```bash
curl -N -X POST http://rockbot-agui/agui -H 'Content-Type: application/json' -d '{
  "threadId": "agui-session", "runId": "run-0005",
  "messages": [ { "id": "m1", "role": "user", "content": "What does this error mean?" } ],
  "forwardedProps": { "rockbot": { "attachments": [
    { "mime": "image/png", "path": "screenshot-1f3a.png", "fileName": "screenshot.png" } ] } }
}'
```

The upload body is one of:

- **`multipart/form-data`** with the file in a part named `file`, which is what a browser's
  `FormData` and .NET's `MultipartFormDataContent` send. `threadId` may be a form field instead of
  a query parameter.
- **The file itself** as the body, with its type as `Content-Type` and its name in the `fileName`
  query parameter: `curl --data-binary @photo.jpg -H 'Content-Type: image/jpeg'
  'http://rockbot-agui/attachments?fileName=photo.jpg'`.

What is accepted:

- **Up to 8 MB**, the agent's own limit. A larger file is `413`, refused before the agent sees it.
- **A type that fits the name.** The file's extension decides its type (`.png`, `.jpg`/`.jpeg`,
  `.gif`, `.webp`, `.bmp`, `.svg`, `.pdf`, `.html`/`.htm`); a `Content-Type` that says otherwise,
  or an extension that isn't in the list, is `415`. `application/octet-stream` and no type at all
  mean "go by the name".
- **What the agent takes.** The agent has the last word and accepts less than that list: images
  (PNG, JPEG, GIF, WebP, BMP) and PDF. Its refusal is `422`, with its reason as `detail`.

| Status | Why |
|---|---|
| `400` | No file part, no file name, an empty file, a body that isn't a readable upload, or no `threadId` when the proxy allows several threads. |
| `413` | The file is over 8 MB. |
| `415` | The file's extension isn't an attachable type, or the declared type isn't the extension's. |
| `422` | The agent refused the file. |

An attachment reference on a run is checked before the message is sent, and a bad one is `400`:

- `path` must be a plain relative path, as an upload returned it: no leading `/`, no `..` or `.`
  segment, no backslash, no `:`.
- `mime` must be the type of the path's extension.
- The file must exist in the attachments directory. (A proxy running without the shared volume
  can't check that and leaves it to the agent.)
- At most 10 attachments on one message.

A client doesn't have to upload to attach: any file already in the attachments directory can be
referenced, such as one the agent attached to an earlier reply.

**Receiving a file.** A reply's attachments arrive in `CUSTOM rockbot.attachments`, right after the
reply's text, in a run and on the notify stream alike:

```
data: {"type":"CUSTOM","name":"rockbot.attachments","value":{"messageId":"7c1e…","attachments":[
  {"mime":"image/png","path":"chart.png","url":"/attachments/chart.png"},
  {"mime":"text/html","path":"previews/ab12cd34/deck.html","fileName":"deck.html","url":"/attachments/previews/ab12cd34/deck.html"}]},…}
```

`url` is relative to the proxy's address. `GET` it for the bytes:

```bash
curl -O http://rockbot-agui/attachments/chart.png
```

The proxy serves only files inside the attachments directory, read-only; anything else is `404`.
The files were written by the agent, a script or a remote MCP server, so treat them as untrusted
content, as the proxy does:

- Every response has `X-Content-Type-Options: nosniff`, and `Content-Type` is the type of the
  file's extension, so a browser never guesses a more dangerous type.
- HTML (`text/html; charset=utf-8`) is sent with `Content-Security-Policy: sandbox allow-scripts`
  and SVG with `Content-Security-Policy: sandbox`. Opened in a browser, such a document runs in
  an origin of its own and can't read anything else served from the proxy's address.
- If your app shows an HTML attachment inline, put it in an `<iframe sandbox="allow-scripts">`
  (never with `allow-same-origin`), as the Blazor UI does. Show an SVG with `<img>`, where it
  can't run script.

### Feedback: `POST /feedback`

```bash
curl -X POST http://rockbot-agui/feedback -H 'Content-Type: application/json' \
  -d '{ "threadId": "agui-session", "messageId": "7c1e…", "isPositive": false }'
```

| Body property | Use |
|---|---|
| `threadId` | The thread the reply is on. |
| `messageId` | The id of the reply, as the client knows it (the `messageId` of its `TEXT_MESSAGE_START`). Required. |
| `isPositive` | `true` for a thumbs up, `false` for a thumbs down. Required. |
| `agentName` | The name on the reply (`TEXT_MESSAGE_START.name`). Defaults to the configured agent. |

The answer is `202`: the proxy has passed it on. A thumbs up is remembered by the agent. On a
thumbs down the agent reconsiders and answers again; that reply arrives on the
[notify stream](#the-notify-stream-get-aguinotify). `400` when `messageId` or `isPositive` is
missing.

### Saved responses

The agent keeps an archive of replies worth finding again: ones the user saved, and the results of
background work it saves by itself (scheduled tasks, subagents). A scheduled task's output is
never part of a thread's history, so this is where a client finds it after the fact.

```bash
curl http://rockbot-agui/saved
```

```json
{ "items": [
  { "id": "a41c…", "label": "Vendor comparison", "agentName": "RockBot",
    "savedAt": "2026-11-14T09:30:00+00:00", "source": "user" },
  { "id": "09be…", "label": "Weekly report", "agentName": "RockBot",
    "savedAt": "2026-11-13T07:00:00+00:00", "source": "scheduled" } ] }
```

`source` says what saved it: `user`, `agent`, or the kind of work (`subagent`, `scheduled`, `a2a`,
`background-task`, `reply`).

```bash
curl http://rockbot-agui/saved/a41c…
```

```json
{ "id": "a41c…", "label": "Vendor comparison", "content": "| Vendor | Price |\n|---|---|\n…",
  "agentName": "RockBot", "savedAt": "2026-11-14T09:30:00+00:00" }
```

```bash
curl -X POST http://rockbot-agui/saved -H 'Content-Type: application/json' \
  -d '{ "threadId": "agui-session", "label": "Vendor comparison", "content": "| Vendor | Price |…" }'
# 201, Location: /saved/a41c…
# { "id": "a41c…" }

curl -X DELETE http://rockbot-agui/saved/a41c…
# 204
```

`POST /saved` takes `label` and `content` (both required, the content being the reply's text),
`threadId`, and optionally `agentName`.

| Status | Why |
|---|---|
| `400` | `POST` without a `label` or `content`. |
| `404` | `GET` or `DELETE` of an id that isn't there. |
| `502` | The agent answered that it could not save. |

### MCP sign-in

An MCP server that protects itself with OAuth refuses the agent until the user has signed in to it.
The agent runs the whole OAuth client and keeps the tokens; a user interface only gets the user's
browser to the sign-in page and back. With this proxy that takes one call from your app.

1. **Find out who needs it.** Ask once at startup, then follow `CUSTOM rockbot.mcp_signin_status`
   on the notify stream.

   ```bash
   curl http://rockbot-agui/mcp-signin
   ```

   ```json
   { "servers": [ { "serverName": "scheduler", "state": "required", "connected": false },
                  { "serverName": "calendar", "state": "signed_in", "connected": true } ] }
   ```

   `state` is `required`, `pending` (a sign-in is under way) or `signed_in`.

2. **Start the sign-in** when the user asks for it.

   ```bash
   curl -X POST http://rockbot-agui/mcp-signin/scheduler/start
   ```

   ```json
   { "serverName": "scheduler", "signInId": "5d0e…",
     "authorizationUri": "https://login.example.com/authorize?response_type=code&client_id=…&state=…",
     "expiresAt": "2026-11-14T09:45:00+00:00",
     "redirectUri": "https://rockbot-agui.example.ts.net/mcp-oauth/callback" }
   ```

3. **Open `authorizationUri`** in the user's browser: a new tab, or the system browser from a
   phone app. The user signs in there.

4. **The browser comes back to the proxy**, at `GET /mcp-oauth/callback`. The proxy hands the code
   to the agent and shows a page saying the tab can be closed. Your app does nothing in this step
   and never sees the code.

5. **The outcome arrives on the notify stream** as `CUSTOM rockbot.mcp_signin_status` with the
   `signInId` from step 2 and a `state` of `completed`, `failed` (with a `message`), `expired` or
   `cancelled` (a newer sign-in to the same server replaced it).

| Status | Why |
|---|---|
| `422` | The agent could not start the sign-in (no such server, or it doesn't use OAuth). `detail` is its reason. |
| `502` | The authorization URL the agent returned has no `state`, so its redirect could not be matched. |
| `504` | The agent didn't answer. Starting a sign-in waits `AgUi:SignInStartTimeout` (90 seconds), since the agent has to find the server's authorization server first. |

#### The redirect URI

The authorization server sends the browser back to **`<the proxy's address>/mcp-oauth/callback`**,
and only if that exact URI is registered as a redirect URI on the MCP server's OAuth client. A
client can't choose another: the proxy always asks for its own. `redirectUri` in the answer to
step 2 shows what was asked for.

The proxy builds it from the address of the request, which is right when clients reach the proxy
directly (`http://rockbot-agui` on a tailnet). **Behind anything that terminates TLS, set
`AgUi:PublicBaseUrl`** (Helm: `agui.publicBaseUrl`) to the address browsers use, for example
`https://rockbot-agui.example.ts.net`. Otherwise the proxy, which sees plain `http` on port 8080,
asks for an `http://` redirect URI and the authorization server refuses the sign-in for not
matching the registered one. The proxy doesn't read `X-Forwarded-*` headers. A value that isn't
an absolute `http(s)` URL stops the proxy at startup.

#### The callback is open, on purpose

`GET /mcp-oauth/callback` is reached by a browser coming from someone else's site, so it can't
require anything of the caller, and it will stay that way when the proxy gets its own sign-in.
What keeps that safe:

- The proxy only relays a redirect whose `state` belongs to a sign-in **it started** and that
  hasn't expired, and only **once**. Anything else gets a "start the sign-in again" page and never
  reaches the agent.
- The code is useless by itself. The agent exchanges it together with a secret (the PKCE verifier)
  that never leaves the agent.
- The page shows nothing it was sent except the authorization server's error code, HTML-encoded.

The sign-ins the proxy has started are kept in memory. If the proxy restarts between steps 2 and 4,
or the redirect lands on another replica, the user sees the "start again" page.

With [app auth](#app-auth) on, starting a sign-in (step 2) needs a token like every other call, so
only an allowed account can put a `state` on the proxy's list. The callback itself stays open, and
relays nothing whose `state` isn't on that list.

### Clearing the context: `POST /threads/{id}/clear`

```bash
curl -X POST http://rockbot-agui/threads/agui-session/clear
```

Clears the thread's conversation on the agent, like `/clear` in the other clients. Long-term memory
is kept. The answer is `202`, and two things follow on the thread's notify stream:

1. `CUSTOM rockbot.context_cleared` (`{"threadId": "agui-session"}`), for **every** client of the
   thread, not only the one that asked. Empty the conversation you show.
2. The agent's confirmation, an ordinary message ("Context cleared — starting fresh.").

What else changes:

- **History starts over.** `GET /threads/{id}/messages` returns the new conversation. The
  broadcast sessions' output from before the clear is left out of this thread's history, so earlier
  scheduled reports don't come back on the next reload. Other threads still have it, and so does the
  saved-response archive (`/saved`). The agent keeps the clear time in memory, so after an agent
  restart the earlier output is back.
- **In-flight work is stopped**, as after a cancel: the agent ends the thread's turn, so an open
  run finishes with `outcome.type: "cancelled"`.
- **Open MCP questions stay** on the proxy's list until the agent says they are closed
  (`rockbot.mcp_question_status`) or they expire. A question belongs to a tool call, not to the
  conversation, and the proxy doesn't guess which calls the clear stopped.
- **The notify buffer is kept.** A client that reconnects with `Last-Event-ID` replays what it
  missed in order, the `rockbot.context_cleared` among it, and ends up where the others are.

### Agent info: `GET /agent`

```bash
curl 'http://rockbot-agui/agent?threadId=agui-session'
```

```json
{ "agentName": "RockBot", "agentVersion": "1.4.0", "threadId": "agui-session",
  "isProcessing": true,
  "work": [ { "taskId": "t1", "kind": "subagent", "description": "Research the vendors",
              "threadId": "agui-session", "startedAt": "2026-11-14T09:30:00+00:00" } ] }
```

For a client that is starting up: the name to show, whether the agent is in the middle of the
thread's turn right now (`isProcessing`), and the thread's running subagents and workers (`work`),
so it can show a busy indicator without having seen the events that started them. After that the
notify stream keeps it current (`SUBAGENT_*`, `rockbot.agent_name`, `rockbot.active_work`).

### Introspection: `GET /agent/introspection`

```bash
curl 'http://rockbot-agui/agent/introspection?sections=models,pricing'
```

```json
{ "agentName": "RockBot", "agentVersion": "0.18.4", "startedAt": "2026-11-15T08:00:00+00:00",
  "models": {
    "loadedAt": "2026-11-15T08:00:01+00:00",
    "summary": "Low: gpt-5.6-luna (Azure AI Foundry)\nBalanced: claude-haiku-5-5 (Anthropic API) …",
    "tiers": [
      { "tier": "Balanced", "acceptsImages": true,
        "models": [ { "provider": "Anthropic", "service": "Anthropic API", "endpointHost": "api.anthropic.com",
                      "modelId": "claude-haiku-5-5", "supportsImageInput": true,
                      "price": { "prefix": "claude-haiku-5-5", "inputPerM": 0.1, "outputPerM": 0.5, "cachedInputPerM": 0.01 } } ] },
      { "tier": "High", "usesTier": "Balanced", "acceptsImages": true, "models": [ … ] }
    ],
    "embedding": { "modelId": "text-embedding-3-small", "service": "Azure AI Foundry", "endpointHost": "…" },
    "decisionModel": { "provider": "Foundry decision API", "service": "Azure AI Foundry", "deployment": "decision-1",
                       "disabledUses": [], "summary": "Decision model: decision-1 (Azure AI Foundry, Foundry decision API)" } },
  "pricing": {
    "loadedFrom": "/data/agent/llm-pricing.json",
    "rows": [ { "prefix": "claude-haiku-5-5", "inputPerM": 0.1, "outputPerM": 0.5, "cachedInputPerM": 0.01 }, … ] } }
```

With `sections=usage` (here for a thread named `agui-session`):

```json
{ "agentName": "RockBot", "agentVersion": "0.18.4",
  "usage": {
    "timeZone": "America/Chicago", "since": "2026-11-15T08:00:00+00:00", "today": "2026-11-15",
    "todayTotals": { "requests": 142, "inputTokens": 3120000, "cachedInputTokens": 2400000,
                     "cacheWriteTokens": 90000, "outputTokens": 41000, "costUsd": 0.61, "unpricedRequests": 0 },
    "last7Days": { … }, "last30Days": { … },
    "days": [ { "date": "2026-11-15", "totals": { … } }, … ],
    "models": [ { "modelId": "claude-haiku-5-5", "totals": { … } }, … ],
    "kinds": [ { "kind": "session", "totals": { … } }, { "kind": "patrol", "totals": { … } }, … ],
    "thread": { "threadId": "agui-session", "firstAt": "…", "lastAt": "…", "totals": { … } } } }
```

What the agent is configured with and what it has used, for an info screen. The agent answers from
its own state without calling its LLM, so this works while the LLM is failing and costs no tokens.

- **`sections`** (optional) is a comma-separated list: `models`, `pricing`, `usage`, `mcp`,
  `skills`, `memory`, `routing`. Without it, every section. A section the agent doesn't know, or can't report, is left out of the answer.
- **`threadId`** (optional) adds that thread's own totals to `usage` (`usage.thread`). It must be
  an allowed thread (`403` otherwise). Left out, it is the only thread when there is one, else none.
- **`models`**: each tier's models in the order the agent tries them (more than one is a fallback
  chain; `usesTier` means the tier has no model of its own), with the pricing row each is costed
  with (no `price` means no row matches and the model is costed at $0), the embedding model and the
  decision model. LLM configuration is read at startup, so this is what runs until the next restart.
- **`pricing`**: the rows, longest prefix first, in USD per million tokens. A model is priced by the
  first row whose prefix its id contains. No `loadedFrom` means the agent is using its built-in
  fallback table. A missing cache rate defaults as described in the agent's pricing file docs: cache
  reads at the input rate, cache writes at 1.25× (5-minute TTL) and 2× (1-hour TTL) input.
- **`usage`**: requests, tokens and estimated cost for today and the last 7 and 30 days; each day
  of the last 30 that had usage (`days`, newest first); the last 30 days by model and by workload
  (`kinds`: `session` for conversations, `patrol` for scheduled tasks, `subagent`, `worker`, and
  `background` for the agent's own work outside any run, such as dreams and memory audits); and the
  thread's totals. Every request the agent sends a provider
  is counted, and a thread's totals include the subagents and workers it started. Days are in the
  agent's home time zone; `since` is when the agent started counting (it keeps the totals in
  `usage-ledger.json` on its volume, 90 days). `unpricedRequests` counts requests whose model no
  pricing row matches, which are costed at $0.
- **`mcp`**: each configured MCP server (`servers`): `name`, `displayName`, `transport` (`http` or
  `stdio`), `host`, `state` (`connected`, `sign-in required`, `sign-in pending`, `failed`,
  `not connected`), `usesOAuth`, tool/prompt/resource counts and `version` while connected, and
  `lastError`/`lastErrorAt` while not (URLs in the error are cut to scheme, host and path). Nothing
  from a server's headers, environment or auth config is included.
- **`skills`**: counts (`count`, `learned`, `starter`, `fromMcp`) and every skill's `name`,
  `summary`, `source` and when it was created, updated and last used.
- **`memory`**: from the latest memory audit (the agent's scheduled check of its long-term memory;
  this never starts one): `status` (`healthy`, `warning`, `alert`), `live`/`archived` counts,
  movement since the audit before, near-duplicate pairs, entries due for purge, `findings`
  (name and message only), the last eval's `evalSoundRate`, and whether dream consolidation is
  paused (`consolidationPaused`, with its reason). Without an audit yet, `auditedAt` is absent.
- **`routing`**: the last 7 days of the tier-routing log: totals, user messages, subagent runs,
  fallbacks, and per tier the count, share (`pct`, 0–100), and average latency, input and output
  tokens and tool calls. The log's prompt previews are not included.

Nothing in the answer is a credential: there are no API keys, and endpoints are a host only. An agent
older than this endpoint doesn't answer, so the proxy answers `504`.

## App auth

The proxy has two modes, like the [Blazor UI](blazor-ui):

- **Network** (the default). No app auth. The network in front of the proxy is the gate.
- **App** (`AgUi:Auth:Enabled`). Every request carries an OIDC ID token, and the proxy lets in
  only the accounts on its allowlist.

Like Blazor's sign-in, this is a doorman and not a tenancy model: everyone who gets in talks to
the same agent, with one history and one memory.

### How a request is authenticated

```bash
curl http://rockbot-agui/threads/agui-session/messages -H "Authorization: Bearer $ID_TOKEN"
```

The token is a signed JWT from the configured issuer, Google by default. The proxy checks, for
every request:

| Check | Against |
|---|---|
| Signature | The issuer's published keys (`{authority}/.well-known/openid-configuration`, then its JWKS). Asymmetric algorithms only. |
| `iss` | The issuer the discovery document names. For Google, `accounts.google.com` without the scheme is accepted too, since Google issues both spellings. |
| `aud` | `AgUi:Auth:Audiences`: the client ids of the apps allowed to call. A token Google issued to any other app is refused. |
| `exp` | The clock, with `AgUi:Auth:ClockSkew` (1 minute) of tolerance. |
| `email_verified` | Must be `true`. A token without the claim is refused (`AgUi:Auth:RequireVerifiedEmail`). |
| `email` | The allowlist: `AgUi:Auth:AllowedEmails` (the whole address, case-insensitive) or `AgUi:Auth:AllowedDomains` (the part after the last `@`, exactly). |

What comes back when a check fails:

| Status | When | Body and headers |
|---|---|---|
| `401` | No token, or one that fails any of the first four checks. | `WWW-Authenticate: Bearer`, with `error="invalid_token"` and a short `error_description` when a token was sent. A problem-details body. The reason in detail is in the proxy's log, not in the response. |
| `403` | A valid token for an account that isn't on the allowlist, or whose address isn't verified. | A problem-details body that says only that the account is not allowed. It never names the account or anything on the allowlist. The proxy logs the address, so an operator can see who to add. |

The proxy fetches the issuer's keys itself, so **the pod needs outbound HTTPS to the issuer**
(`accounts.google.com` and `www.googleapis.com` for Google). It caches them and fetches again when
a token names a key it doesn't know.

**ID tokens, not access tokens.** A Google access token is an opaque string, not a JWT, and
checking one means asking Google about it on every request. The proxy doesn't do that. Any OIDC
provider's JWT works, ID token or access token, as long as its `aud` is one of the configured
audiences and it carries `email` and `email_verified`.

**The allowlist is checked on every request**, not once per sign-in, and it is read at startup:
taking someone off it takes a restart (a `helm upgrade` does one).

### Getting a token

The proxy never asks anyone to sign in and has no login page. The app does that, with Google's
own libraries, and sends what it gets.

- **A browser or WebAssembly app.** Use Google Identity Services. Create a **Web application**
  OAuth client whose *Authorized JavaScript origins* list the app's origin, call
  `google.accounts.id.initialize({ client_id, callback })`, and send the `credential` the callback
  receives as the bearer token. A Blazor WebAssembly app can use
  `Microsoft.AspNetCore.Components.WebAssembly.Authentication` with authority
  `https://accounts.google.com` instead; take the **ID token** from it, not the access token.
- **An Android or iOS app.** Use Credential Manager (Android) or Google Sign-In (iOS) and ask for
  an ID token. Its `aud` is the client id you ask with, so that id goes in `AgUi:Auth:Audiences`.
- **A .NET client.** Put the token on the `HttpClient`:

  ```csharp
  httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
  var chat = new AGUIChatClient(new AGUIChatClientOptions(httpClient, "https://agui.example.com/agui"));
  ```

There is **no client secret** anywhere: the proxy only validates tokens.
[`deploy/agui-auth-setup.md`](https://github.com/MarimerLLC/rockbot/blob/main/deploy/agui-auth-setup.md)
has the Google console steps.

A Google ID token lasts an hour. Get a new one before it runs out (Google Identity Services and
the mobile libraries do this without asking the user again), and expect `401` when one has.

### Token expiry and open streams

- **The notify stream ends when the token it was opened with expires.** The proxy closes it; the
  client reconnects with a fresh token and `Last-Event-ID`, and misses nothing (see
  [Reconnecting](#reconnecting)). A stream is never open longer than its credential is good.
- **A run is allowed to finish.** A run is authorized when it starts and lasts at most
  `UserProxy:MaxTurnDuration` (30 minutes). Cutting it at expiry would cancel the turn on the
  agent, since a dropped run is a cancel, so the proxy doesn't.

### What the agent sees

The agent gets a user id with each message. In app mode it is the caller's email address, in
lower case; in network mode it is `AgUi:DefaultUserId`. The agent keys nothing on it: it is one
agent with one memory for everyone the proxy lets in. The id is written to the agent's log and to
the turn's telemetry (`rockbot.user.id`), which is what it is for: seeing who sent a message.
Set `AgUi:Auth:UserIdClaim` to `sub` to send the provider's opaque account id instead.

### Access tickets: `POST /tickets`

Two requests can't carry an `Authorization` header, because the browser makes them, not a script:

- an attachment in `<img src>`, in a link, or opened in a new tab;
- the notify stream opened with `EventSource`, which has no way to set headers.

For those, ask for a **ticket**: a URL that works by itself, for one resource, for a few minutes.

```bash
curl -X POST http://rockbot-agui/tickets -H "Authorization: Bearer $ID_TOKEN" \
  -H 'Content-Type: application/json' -d '{ "kind": "attachment", "path": "previews/ab12cd34/deck.html" }'
```

```json
{ "url": "/t/v1.eyJrIjoiYSIs….Q0x5…/attachments/previews/ab12cd34/deck.html",
  "ticket": "v1.eyJrIjoiYSIs….Q0x5…", "expiresAt": "2026-11-14T09:35:00+00:00" }
```

| Body | Ticket for |
|---|---|
| `{"kind": "attachment", "path": "…"}` | That attachment. `path` is the attachment's `path`, as `rockbot.attachments`, history or `POST /attachments` gave it. |
| `{"kind": "notify", "threadId": "…"}` | That thread's notify stream. The `url` is `/agui/notify?threadId=…&ticket=…`; add `&lastEventId=…` when reconnecting. |

`url` is relative to the proxy. Use it at once, as the `src` of an image, the `href` of a link, or
the URL of an `EventSource`. What a ticket is and isn't:

- **Short-lived.** `AgUi:Auth:TicketLifetime`, 5 minutes, and never beyond the expiry of the token
  it was asked with. It can be used any number of times until then.
- **For one thing.** A ticket is good for `GET` on the resource it names and is not a credential
  anywhere else: not on another endpoint, not for another attachment or thread, not as a bearer
  token, and not to get another ticket.
- **An HTML attachment's ticket also covers the files beside it.** A deck at
  `previews/ab12cd34/deck.html` loads `slide1.png` from its own directory by a relative link. The
  ticket is in the path of the URL, so the browser's request for the image carries it too, and it
  is accepted for anything under `previews/ab12cd34/`. Only HTML attachments in a directory get
  this; an image's ticket is for the image.
- **A stream opened with one ends when the token would have.** `EventSource` then reports an
  error. Reconnecting with the same URL fails with `401`, and `EventSource` doesn't retry after
  that: ask for a new ticket and open a new `EventSource`, with `lastEventId` in the query.
- **Not stored.** The ticket is signed with a key the proxy makes at startup, so a restart
  refuses the tickets in flight. Ask again. (With more than one replica, give them one key:
  `AgUi:Auth:TicketKey`.)

A ticketed attachment is served with `Referrer-Policy: no-referrer` and `Cache-Control: private,
no-store`, on top of the headers every attachment has, so the URL doesn't travel to sites the
document links to.

Why not put the token in the URL, or sign the `url` in events? A URL ends up in browser history,
proxy logs and `Referer` headers, so nothing long-lived belongs in one. And events are replayed
from the notify buffer for a day and from history indefinitely: a signed URL inside one would
have to stay valid as long.

A client that fetches with a script doesn't need tickets. `fetch` with the header, then
`URL.createObjectURL(await response.blob())`, shows an image; a fetch-based SSE reader (what
`AGUIChatClient` and most AG-UI clients use) can send the header on the notify stream.

In network mode `POST /tickets` answers with the plain URL (`{"url": "/attachments/…"}`) and no
ticket, so a client written for app mode works against a proxy without it.

## CORS

A browser app served from another origin than the proxy can call it only if the proxy names that
origin. List the origins in `AgUi:Cors:AllowedOrigins` (Helm: `agui.cors.allowedOrigins`):

```yaml
agui:
  cors:
    allowedOrigins:
      - https://app.example.com
      - http://localhost:5173       # a dev server
```

- **None configured (the default): no CORS headers at all.** Only pages served from the proxy's
  own origin and non-browser clients (a phone app, `curl`, a server) can use the API. A request
  from any other origin is refused; see [The origin check](#the-origin-check).
- An origin is `scheme://host[:port]`, with no path. There is no wildcard, and `*` stops the
  proxy at startup.
- Allowed origins may use `GET`, `POST` and `DELETE`, send any request header (`Authorization`,
  `Content-Type`, `Last-Event-ID` and whatever a client library adds), and read the `Location`
  and `WWW-Authenticate` response headers. Preflight requests are answered without a token and
  cached by the browser for 10 minutes.
- The event streams (`POST /agui`, `GET /agui/notify`) carry the headers like any other response,
  and so do `401` and `403`, so the app can read why it was refused.
- The proxy never sends `Access-Control-Allow-Credentials`: it has no cookies. Use
  `fetch(url, { headers: { Authorization: … } })`, not `credentials: "include"`.

### The origin check

CORS headers only decide which pages may *read* a response. A browser still *sends* some
cross-site requests without asking first: a form post, a `text/plain` or multipart body, a `POST`
with no body. In network mode nothing else asks who is calling, so without a check any web page,
open in a browser that can reach the proxy, could start a run, cancel one, clear the context or
upload a file.

So the proxy checks the `Origin` header itself, **in both modes and whether or not any CORS origin
is configured**. A request to a client endpoint that names an origin is answered `403` (a
problem-details body, before anything else looks at the request) unless the origin is:

- one of `AgUi:Cors:AllowedOrigins`; or
- the proxy's own: the origin of `AgUi:PublicBaseUrl` when that is set, or any origin whose host
  and port are the request's `Host` header. The scheme isn't compared in the second case, because
  behind an ingress that terminates TLS the browser says `https` and the proxy sees `http`.

What that means in practice:

| Request | Result |
|---|---|
| No `Origin` header: `curl`, a phone app, a server, a link or new tab, an `<img>` | Passes, as before. |
| A page served from the proxy's own address | Passes. |
| A page on a listed origin | Passes, with CORS headers. |
| A page on any other origin, any method | `403`. |
| `Origin: null`: a sandboxed document (an HTML attachment is served sandboxed), a `file://` page, a request sent with `Referrer-Policy: no-referrer` | `403`. `null` can't be listed. |

`GET /healthz` and `GET /mcp-oauth/callback` are not checked. If an ingress rewrites the `Host`
header, a page served from the proxy's own address is refused until you set `AgUi:PublicBaseUrl`.
A script in an HTML attachment can load images beside it, but not module scripts or fonts, which
a browser fetches with `Origin: null`.

What the check doesn't cover:

- **It is not authentication.** A program that isn't a browser sends whatever `Origin` it likes,
  or none. In network mode the network is still the gate.
- **A browser that doesn't send `Origin` on a cross-site `POST`.** Every current browser does. A
  few from before 2020 don't.
- **DNS rebinding.** A page that points its own hostname at the proxy's address is same-origin as
  far as the browser, and this check, can tell. Set `AllowedHosts` (ASP.NET Core's host filter;
  environment variable `AllowedHosts`, for example `rockbot-agui;rockbot-agui.example.ts.net`) so
  the proxy only answers to its real names, or use app auth, where a page without a token gets
  nothing either way.

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
| `AgUi:DefaultUserId` | `agui-user` | The user id sent to the agent for every caller in network mode. In app mode it is the caller's own; see [What the agent sees](#what-the-agent-sees). |
| `AgUi:Auth:Enabled` | `false` | [App auth](#app-auth). `true` requires a bearer token on every client endpoint. |
| `AgUi:Auth:Authority` | `https://accounts.google.com` | The OIDC issuer whose tokens are accepted. Must be `https`. |
| `AgUi:Auth:Audiences` | | **Required with auth on.** The client ids (`aud`) of the apps allowed to call. |
| `AgUi:Auth:AllowedEmails`, `AgUi:Auth:AllowedDomains` | | **One entry required with auth on.** Who gets in. |
| `AgUi:Auth:ValidIssuers` | | Issuers accepted besides the one the authority's discovery document names. Rarely needed. |
| `AgUi:Auth:RequireVerifiedEmail` | `true` | Refuse a token that doesn't say `email_verified: true`. |
| `AgUi:Auth:UserIdClaim` | `email` | The claim sent to the agent as the user id. |
| `AgUi:Auth:ClockSkew` | `00:01:00` | Tolerance when checking a token's lifetime. |
| `AgUi:Auth:TicketLifetime` | `00:05:00` | How long an [access ticket](#access-tickets-post-tickets) is good for. At most one hour. |
| `AgUi:Auth:TicketKey` | | Base64 of at least 32 bytes: the key tickets are signed with. Empty makes a random key at startup. Only needed so several replicas accept each other's tickets. |
| `AgUi:Cors:AllowedOrigins` | | The browser origins that may call the API. See [CORS](#cors). |

With `AgUi:Auth:Enabled` on, a configuration that can't work stops the proxy at startup instead of
leaving it open or locked: no allowlist entry, no audience, an authority that isn't `https`, a
ticket key that is too short. The message names every setting that is wrong.
| `AgUi:KeepAliveInterval` | `00:00:15` | How often an idle run or notify stream gets an SSE comment. Zero or less turns it off. |
| `AgUi:ResumeReplyTimeout` | `00:05:00` | How long a run with `resume` waits for the agent's reply after the agent accepted an answer, before it ends with `rockbot.reply_pending`. |
| `AgUi:RequestTimeout` | `00:00:30` | How long a REST endpoint waits for the agent's answer (agent info, introspection, saved responses, the sign-in list, an upload) before it answers `504`. |
| `AgUi:SignInStartTimeout` | `00:01:30` | How long `POST /mcp-signin/{server}/start` waits for the agent. |
| `AgUi:PublicBaseUrl` | | The address browsers reach the proxy at. The MCP sign-in redirect URI is built from it; see [The redirect URI](#the-redirect-uri). Empty uses the address of the request. |
| `UserProxy:AttachmentUploadUrl` | | The agent's upload endpoint (`http://<agent>:8082`). When set, an uploaded file goes to the agent over HTTP; otherwise, or if that fails, over the message bus. |
| `ROCKBOT_SHARED_PATH` (environment) | `/rockbot/shared` | Where the shared volume is mounted. Attachments are served from its `attachments` directory. |
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
  publicBaseUrl: ""             # set when TLS is terminated in front: https://rockbot-agui.<tailnet>.ts.net
  tailscale:
    hostname: rockbot-agui      # http://rockbot-agui on your tailnet
    ingress:
      enabled: false            # true: https://rockbot-agui.<tailnet>.ts.net
```

That is network mode, private to the tailnet. For an API on the internet, with app auth:

```yaml
agui:
  enabled: true
  publicBaseUrl: "https://agui.example.com"
  auth:
    enabled: true
    audiences:
      - 1234567890-abcdef.apps.googleusercontent.com      # the web app's client id
      - 1234567890-ghijkl.apps.googleusercontent.com      # the Android app's
    allowedEmails:
      - someone@example.com
    allowedDomains: []
  cors:
    allowedOrigins:
      - https://app.example.com
  ingress:
    enabled: true
    className: nginx
    host: agui.example.com
    annotations:
      cert-manager.io/cluster-issuer: letsencrypt-prod
      nginx.ingress.kubernetes.io/proxy-read-timeout: "3600"
      nginx.ingress.kubernetes.io/proxy-buffering: "off"
      nginx.ingress.kubernetes.io/proxy-body-size: "9m"
    tls:
      enabled: true
      secretName: rockbot-agui-tls
```

| Value | Default | Use |
|---|---|---|
| `agui.enabled` | `false` | Deploy the proxy |
| `agui.image.repository`, `agui.image.tag` | `rockylhotka/rockbot-agui`, `latest` | The image |
| `agui.replicaCount` | `1` | Replicas. Each has its own identity on the bus, so a run works on any of them. Each also has its own notify buffer, so with more than one a reconnect that lands on another replica is told to resync. |
| `agui.replyTimeout` | `""` | `UserProxy:ReplyTimeout`; empty keeps 3 minutes |
| `agui.allowedThreadIds` | `[]` | `AgUi:AllowedThreadIds` |
| `agui.publicBaseUrl` | `""` | `AgUi:PublicBaseUrl`. Needed for MCP sign-in whenever clients reach the proxy over HTTPS (`agui.tailscale.ingress.enabled` or `agui.ingress.enabled`). |
| `agent.attachmentUpload.enabled` | `true` | The agent's own value. When on, the proxy gets `UserProxy:AttachmentUploadUrl` and uploads go to the agent over HTTP instead of through RabbitMQ. |
| `agui.tailscale.hostname`, `.tags`, `.ingress.enabled`, `.ingress.proxyGroup` | `rockbot-agui`, … | Tailnet exposure, as for `blazor.tailscale` |
| `agui.ingress.enabled`, `.className`, `.host`, `.annotations`, `.tls` | off | An ordinary HTTPS ingress |
| `agui.ingress.allowUnauthenticated` | `false` | Lets that ingress render without `agui.auth.enabled` |
| `agui.auth.enabled` | `false` | [App auth](#app-auth) |
| `agui.auth.authority` | `""` | `AgUi:Auth:Authority`; empty is Google |
| `agui.auth.audiences` | `[]` | `AgUi:Auth:Audiences`. Required with auth on. |
| `agui.auth.allowedEmails`, `agui.auth.allowedDomains` | `[]` | The allowlist. One entry required with auth on. |
| `agui.auth.ticketKeyFromSecret`, `secrets.agui.ticketKey` | `false`, `""` | One ticket-signing key for several replicas. Leave off with one replica. |
| `agui.cors.allowedOrigins` | `[]` | `AgUi:Cors:AllowedOrigins`. See [CORS](#cors). |

These are the proxy's own settings and share nothing with `blazor.auth`: the two proxies read
different configuration keys (`AgUi:Auth:*` and `Auth:*`), have different client ids, and can have
different allowlists. App auth needs no Secret, because the proxy only validates tokens.

By default the Service is a Tailscale LoadBalancer, private to the tailnet, and that is the gate.
The chart refuses to render the ordinary HTTPS ingress unless something gates it: either
`agui.auth.enabled`, or `agui.ingress.allowUnauthenticated: true`, which states that something
outside the chart authenticates requests. It also refuses `agui.auth.enabled` with an empty
allowlist or no audience, as the proxy itself does at startup.

An ingress in front of the proxy must not buffer responses and needs a read timeout longer than a
run; `values.yaml` shows the nginx annotations. The notify stream stays open indefinitely, with a
comment every 15 seconds, so an idle timeout shorter than that would cut it. With app auth, the
pod also needs to reach the token issuer over HTTPS: allow it in any egress NetworkPolicy.

The chart has no values for the notify buffer, the timeouts other than `replyTimeout`, or the less
common auth settings. To change them, set the `AgUi__NotifyBufferSize`, `AgUi__NotifyBufferAge`,
`AgUi__NotifySubscriberQueueSize`, `AgUi__ResumeReplyTimeout`, `AgUi__RequestTimeout`,
`AgUi__SignInStartTimeout`, `AgUi__Auth__RequireVerifiedEmail`, `AgUi__Auth__UserIdClaim` or
`AgUi__Auth__TicketLifetime` environment variables on the deployment.

The pod mounts the chart's shared volume (the one the agent writes attachments to) **read-only**
at `/rockbot/shared`, to serve `GET /attachments/{path}`. It writes nothing there: an upload goes
to the agent, which does the writing. The pod has no volume of its own. The notify buffer, the
open-question list and the sign-ins under way are in memory, so they are empty after a restart and
clients are told to resync. Its queues on the broker are ephemeral: they are deleted when the pod
stops, and the broker expires them if it crashes.

An ingress in front of the proxy has to let an 8 MB upload through. nginx's default body limit is
1 MB: set `nginx.ingress.kubernetes.io/proxy-body-size: "9m"` among `agui.ingress.annotations`.

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

With [app auth](#app-auth) on, the client sends whatever its `HttpClient` sends, so put the token
there (`DefaultRequestHeaders.Authorization`, or a `DelegatingHandler` that refreshes it). In a
browser, `EventSource` can't send the header: open the notify stream with a
[ticket](#access-tickets-post-tickets), or read it with `fetch`.

### A reference client

[`samples/RockBot.AgUi.WasmClient`](https://github.com/MarimerLLC/rockbot/tree/main/samples/RockBot.AgUi.WasmClient)
is a small Blazor WebAssembly chat app written against this page: runs, the notify stream with
its reconnect rules, history, MCP questions in `custom` mode, uploads and attachments, and
(optionally) Google sign-in for app mode. It is a sample to read and copy from, not a supported
UI; the [Blazor UI](blazor-ui) is still RockBot's own.

It comes in three parts:

- **`samples/RockBot.AgUi.Client`** is the transport, with no browser dependency. It reads both
  event streams with one parser and maps each AG-UI event to the chat events of
  [`RockBot.UserProxy.ChatState`](blazor-ui#chat-state-rockbotuserproxychatstate), the state
  model the Blazor UI uses. A .NET client of another kind (MAUI, a console app) can start from it.
- **`samples/RockBot.AgUi.WasmClient`** is the page that renders that state.
- **`samples/RockBot.AgUi.DevHost`** runs this proxy with an in-process message bus and a
  scripted agent, so a client can be developed with no RabbitMQ, LLM or cluster:

  ```bash
  dotnet run --project samples/RockBot.AgUi.DevHost       # the proxy, http://localhost:5140
  dotnet run --project samples/RockBot.AgUi.WasmClient    # the sample, http://localhost:5180
  ```

To point it at a deployed proxy, set `AgUi:BaseUrl` in the sample's `wwwroot/appsettings.json`
and add the sample's origin to the proxy's [`AgUi:Cors:AllowedOrigins`](#cors). Published, the
sample is static files for any web server; it has no image and is not in the Helm chart. The
sample's
[README](https://github.com/MarimerLLC/rockbot/blob/main/samples/RockBot.AgUi.WasmClient/README.md)
has the details, what it leaves out, and the state of its app-mode sign-in (written, not tested
against Google).
