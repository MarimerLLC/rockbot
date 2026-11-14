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
client ──POST /agui──► RockBot.UserProxy.AgUi ──RabbitMQ──► agent
       ◄──── SSE ─────                        ◄───────────
```

This page describes what is built so far: runs, cancelling, and history. Output nobody asked for
(subagent results, scheduled tasks), MCP question forms, attachments, sign-in and CORS come in later
phases of [#794](https://github.com/MarimerLLC/rockbot/issues/794). The full design is in
[`design/userproxy-agui.md`](https://github.com/MarimerLLC/rockbot/blob/main/design/userproxy-agui.md).

> **The proxy has no sign-in of its own yet.** Whoever can reach it can talk to the agent as its
> user. Run it on a private network such as a tailnet, or behind a gateway that authenticates. It
> sends no CORS headers either, so a browser app on another origin can't call it directly yet.

## Endpoints

| Endpoint | Does |
|---|---|
| `POST /agui` | Runs one turn. The body is an AG-UI `RunAgentInput`; the response is the run's event stream. |
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
- Subagent and worker events (`SUBAGENT_*`, their progress and their results) are not in the
  stream yet. They belong to the notify stream of a later phase.
- A cancelled run ends with `RUN_FINISHED` and `outcome.type: "cancelled"`.
- `RUN_ERROR.code` is one of:

  | Code | Meaning |
  |---|---|
  | `error` | The agent reported that the turn failed. |
  | `timeout` | The agent never started the run, or the run passed its maximum duration. The agent may still be working. |
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
| `UserProxy:ReplyTimeout` | `00:03:00` | How long a run waits for the agent to start it. Also the wait for a history request. |
| `UserProxy:MaxTurnDuration` | `00:30:00` | The longest a run may last while the agent keeps sending heartbeats |
| `AgUi:AllowedThreadIds` | `["agui-session"]` | The thread ids clients may use. Others get `403`. Setting any replaces the default. |
| `AgUi:DefaultUserId` | `agui-user` | The user id sent to the agent for every caller |
| `AgUi:KeepAliveInterval` | `00:00:15` | How often an idle run stream gets an SSE comment. Zero or less turns it off. |

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
| `agui.replicaCount` | `1` | Replicas. Each has its own identity on the bus, so a run works on any of them. |
| `agui.replyTimeout` | `""` | `UserProxy:ReplyTimeout`; empty keeps 3 minutes |
| `agui.allowedThreadIds` | `[]` | `AgUi:AllowedThreadIds` |
| `agui.tailscale.hostname`, `.tags`, `.ingress.enabled`, `.ingress.proxyGroup` | `rockbot-agui`, … | Tailnet exposure, as for `blazor.tailscale` |
| `agui.ingress.enabled`, `.className`, `.host`, `.annotations`, `.tls` | off | An ordinary HTTPS ingress |
| `agui.ingress.allowUnauthenticated` | `false` | Required to be `true` for that ingress to render |

By default the Service is a Tailscale LoadBalancer, private to the tailnet. Because the proxy has no
sign-in, the chart refuses to render the ordinary HTTPS ingress unless
`agui.ingress.allowUnauthenticated` is `true`, which states that something outside the chart
authenticates requests. An ingress in front of the proxy must not buffer responses and needs a read
timeout longer than a run; `values.yaml` shows the nginx annotations.

The pod needs no volume. Its queues on the broker are ephemeral: they are deleted when the pod
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
