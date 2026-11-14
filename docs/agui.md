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
(subagent results, scheduled tasks, late replies), cancelling, and history. Answering MCP question
forms, attachments, sign-in and CORS come in later phases of
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
- `tools`, `state` and `context` are ignored. `resume` is refused: interrupts aren't supported yet.

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
  | `agent_lost` | The run started, then the agent went silent. |
  | `client_error` | The proxy itself failed, for example it could not publish to the bus. |
  | `proxy_stopping` | The proxy is shutting down (a rollout). The agent finishes the turn; read the reply from the thread's history. |

While nothing else is being sent, the proxy writes an SSE comment (`: keepalive`) every 15 seconds,
so proxies and mobile networks don't drop the connection. SSE clients skip comments.

A request the proxy can't run is refused with an ordinary HTTP error (a problem-details JSON body)
before any event is sent:

| Status | Why |
|---|---|
| `400` | The body isn't a `RunAgentInput`, has no `threadId` or `runId`, has no user message with text, or has `resume`. |
| `403` | The `threadId` is not allowed. |
| `409` | A run with this `runId` is still in progress. |
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
| A reply nobody is waiting for: a scheduled task's output, the agent's answer after a subagent finished, an A2A result, a late reply | `TEXT_MESSAGE_START` / `CONTENT` / `END`, `CUSTOM rockbot.attachments` when it has any, then `CUSTOM rockbot.origin` with `messageId`, `origin` (`channel`, `promptSummary`, `startedAt`, `sessionId`) and `source` |
| An MCP server's question, as a form | `CUSTOM rockbot.mcp_question`: `form` (the [`McpQuestionForm`](https://github.com/MarimerLLC/rockbot/blob/main/src/RockBot.UserProxy.Abstractions/McpQuestionForm.cs)), `text` (the same question as plain text), `sessionId`, `agentName`. Only a thread whose runs declared `InteractiveForms` gets forms. The proxy has no way to answer one yet. |
| A question changed state (answered, declined, expired, rejected…) | `CUSTOM rockbot.mcp_question_status`: `sessionId`, `questionId`, `state`, `answeredBy`, `reason` |
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

### Cancelling

There are two ways to cancel, and both stop **all** of the thread's in-flight work on the agent:

- **Abort the request** (close the connection). This is the AG-UI convention. The proxy sends the
  agent a cancel request for the thread.
- **`POST /threads/{id}/cancel`** from another connection. The open run then ends with
  `RUN_FINISHED` and `outcome.type: "cancelled"` once the agent reports it.

A client that loses its connection therefore cancels its turn. A phone that drops off the network
mid-run should send the message again.

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
| `UserProxy:ReplyTimeout` | `00:03:00` | How long a run waits for the agent to start it, before it ends with `rockbot.reply_pending`. Also the wait for a history request. |
| `UserProxy:MaxTurnDuration` | `00:30:00` | The longest a run may last while the agent keeps sending heartbeats. After that it ends with `rockbot.reply_pending`. |
| `AgUi:AllowedThreadIds` | `["agui-session"]` | The thread ids clients may use. Others get `403`. Setting any replaces the default. |
| `AgUi:DefaultUserId` | `agui-user` | The user id sent to the agent for every caller |
| `AgUi:KeepAliveInterval` | `00:00:15` | How often an idle run or notify stream gets an SSE comment. Zero or less turns it off. |
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

The chart has no values for the notify buffer. To change it, set the `AgUi__NotifyBufferSize`,
`AgUi__NotifyBufferAge` or `AgUi__NotifySubscriberQueueSize` environment variables on the deployment.

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

`AGUIChatClient` only runs turns. For the notify stream use any server-sent events client, for
example `System.Net.ServerSentEvents.SseParser` in .NET or `EventSource` in a browser, and read
each event's `data` as an AG-UI event with the SDK's `AGUIJsonSerializerContext.Default.BaseEvent`.
