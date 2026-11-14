# AG-UI User Proxy

**Status:** Phases 1 (runs) and 2 (notify stream) implemented, 2026-11-14. Phases 3 to 6 are not
started. See
[Implementation status](#implementation-status) for what was built and where it differs from this
design; user documentation is in [`docs/agui.md`](../docs/agui.md).

## Problem

Every RockBot client today reaches the agent over RabbitMQ through `UserProxyService`:

- **The CLI** (`RockBot.UserProxy.Cli`) is a terminal process with its own proxy.
- **The Blazor UI** (`RockBot.UserProxy.Blazor`) is a Blazor Server app. It hosts the proxy and renders on the
  server, and the browser sees only the SignalR circuit.

Neither can serve a client that runs somewhere else: a Blazor WebAssembly app, a phone app, or a
third-party agent front end. Those clients can't (and shouldn't) open a broker connection, so they
need an HTTP API in front of the agent. Inventing that API means also inventing its client
libraries.

[AG-UI](https://docs.ag-ui.com/) is an open protocol for exactly this hop, between an agent backend
and a user-facing app. It has a 1.0 spec and a .NET SDK. Using it gets RockBot a documented wire
format, ready-made clients (`AGUIChatClient` in .NET, CopilotKit and others in JavaScript), and
compatibility with front ends we don't write.

## Goal

A new user proxy, `RockBot.UserProxy.AgUi`, that exposes the agent over AG-UI. It's a user proxy
like the CLI and Blazor: it owns a `UserProxyService`, implements `IUserFrontend`, and publishes
and subscribes on the same `user.*` topics. The difference is that its UI lives out of process and
talks to it over HTTP.

```
Blazor WASM / mobile / third-party ──AG-UI (HTTP POST → SSE)──┐
                                    ──notify stream (SSE)──────┤
                                    ──REST───────────────────── RockBot.UserProxy.AgUi ──RabbitMQ──► Agent
CLI ───────────────────────────────────────────────────────────── (UserProxyService) ──RabbitMQ──► Agent
Browser ──SignalR──► RockBot.UserProxy.Blazor ──────────────────── (UserProxyService) ──RabbitMQ──► Agent
```

### Non-goals

- **Replacing the Blazor Server UI.** It stays on the bus. A WASM client on this proxy may later
  replace it, but that's a separate decision.
- **Token streaming.** The agent doesn't stream today: `AgentLoopRunner` and `UserMessageHandler`
  call the non-streaming `GetResponseAsync`, and the bus carries whole replies. This proxy emits each reply as one text
  chunk. Streaming is a separate, agent-side change that every proxy benefits from (see
  *Later: richer bus events*).
- **Multi-tenancy.** Like Blazor, this is a doorman, not a tenancy model (`docs/blazor-ui.md`).
  Everyone who gets in shares the agent's single user identity and memory.
- **Agent-to-agent use.** That's A2A (`RockBot.A2A.Gateway`). AG-UI is for user interfaces.

## Background: AG-UI 1.0 and the .NET SDK

Facts as of 2026-11-12. Re-check versions when work starts.

- **Spec 1.0.** One request starts one *run*: the client POSTs `RunAgentInput` (`threadId`,
  `runId`, `messages`, `tools`, `state`, `forwardedProps`, `resume`), and the server answers with
  an event stream for that run. HTTP + SSE is mandatory; HTTP + protobuf is optional.
- **Event families.**
  - Lifecycle: `RUN_STARTED`, `RUN_FINISHED`, `RUN_ERROR`, `STEP_*`.
  - Text: `TEXT_MESSAGE_START` / `CONTENT` / `END`.
  - Tools: `TOOL_CALL_START` / `ARGS` / `END` / `RESULT`.
  - Reasoning: `REASONING_*`.
  - State: `STATE_SNAPSHOT`, `STATE_DELTA` (JSON Patch), `MESSAGES_SNAPSHOT`.
  - Activity: `ACTIVITY_SNAPSHOT` / `DELTA`.
  - Subagents: `SUBAGENT_STARTED` / `FINISHED` / `ERROR`.
  - Extension points: `CUSTOM`, `RAW`.
- **Human in the loop.** A run can end with `RUN_FINISHED { outcome: { type: "interrupt",
  interrupts: [...] } }`. Each interrupt has an `id`, `reason` (`input_required`, `confirmation`,
  `tool_call`, or a namespaced custom one), `message`, `responseSchema` and `expiresAt`. The client
  resumes with a new run on the same thread whose `resume` array must address **every** open
  interrupt (`resolved` with a payload, or `cancelled`). The spec says pending interrupts block new
  input.
- **No side channel.** The spec states that everything the user sees arrives in a run's stream.
  There is no way for the server to push something the client didn't ask for.
- **Compatibility.** New event types and optional fields are additive. Unknown events are dropped
  with a warning. The protocol version travels in `RunAgentInput.protocolVersion` and
  `RUN_STARTED`.
- **.NET SDK 1.0** (ag-ui-protocol, MIT; 1.0.0 on 2026-09-17, 1.0.1 on 2026-10-12). Packages:
  - `AGUI.Abstractions`: event types, built on `Microsoft.Extensions.AI.Abstractions`.
  - `AGUI.Server`: `AsAGUIEventStreamAsync` turns an `IChatClient` streaming response into a run;
    `TypedResults.ServerSentEvents` serves it.
  - `AGUI.Client`: `AGUIChatClient`, an `IChatClient` that targets netstandard2.0, so it runs in
    WASM and MAUI.
  - `AGUI.Formatting` and `AGUI.Protobuf` (protobuf covers a subset of events).

  The Microsoft Agent Framework now builds on these packages rather than on its own copy.

### We use the types, not the server adapter

`AsAGUIEventStreamAsync` assumes an `IChatClient` running in the proxy's process. RockBot's LLM
loop runs in the agent, on the other side of the bus, and arrives as `AgentReply` envelopes rather
than `ChatResponseUpdate`s. So the proxy builds AG-UI events itself, using `AGUI.Abstractions` for
the event types and serialization. A thin `IChatClient` facade over the bus, used only to reuse the
adapter, would hide the parts that don't fit (unsolicited replies, questions) and isn't worth it.

## Fit: what maps, what doesn't

| RockBot today | AG-UI | Fit |
|---|---|---|
| `UserMessage` → correlated final `AgentReply` | One run: text message, then `RUN_FINISHED` | Good |
| Correlated non-final `AgentReply` (tool progress strings) | `ACTIVITY_SNAPSHOT` / `ACTIVITY_DELTA` | Good, though only as text |
| `CancelSessionRequest` | Client aborts, or `RUN_FINISHED` with outcome `cancelled` | Good |
| Conversation history | `MESSAGES_SNAPSHOT` | Good |
| `McpQuestionForm` / `McpQuestionAnswer` | Interrupt + `resume` | Partial; different blocking rules (see *MCP questions*) |
| Subagent and worker progress | `SUBAGENT_*` / activity | Partial; the bus has no start/finish or parent tool call |
| Unsolicited replies (`ReplyOrigin`): subagent results, scheduled tasks, watches, A2A, restart notices | Nothing; the spec has no side channel | **Gap**, filled by the notify stream |
| `McpQuestionStatus`, `McpSignInStatus`, agent name change | Nothing | **Gap**, filled by the notify stream |
| Saved responses, feedback, uploads, MCP sign-in, clear context, agent info | Nothing | Plain REST |
| `ClientCapabilities` | Nothing; AG-UI assumes a rich client | Carried in `forwardedProps` |
| `AgentAttachment` on replies | Nothing outbound | `CUSTOM` event + attachment endpoint |

The core mapping is a good fit. The gaps all trace back to RockBot producing output nobody asked
for, which AG-UI deliberately doesn't model.

## Design

### Project and hosting

- **`src/RockBot.UserProxy.AgUi`.** An ASP.NET Core minimal-API app (net10.0) referencing
  `RockBot.UserProxy`, `RockBot.Messaging.RabbitMQ`, `AGUI.Abstractions` and `AGUI.Formatting`.
  No Razor.
- **Identity on the bus.**
  - `ProxyId`: `agui-<pod name>`, unique per replica.
  - `ChannelName`: `"agui"`, so `ReplyOrigin` anchors say where work started.
  - `EphemeralQueues`: false.
- **Deployment.** Its own deployment and image (`rockylhotka/rockbot-agui`), under Helm values
  `agui.*` (`enabled`, `image`, `tailscale`, `ingress`, `auth`, `cors.allowedOrigins`), mirroring
  `blazor.*`. It's stateless apart from the in-memory notify buffer, so it needs no PVC beyond a
  data-protection key ring if auth needs one.

### Endpoints

| Endpoint | Kind | Maps to |
|---|---|---|
| `POST /agui` | AG-UI run (SSE) | `SendAsync`, `SendMcpAnswerAsync` (resume) |
| `GET /agui/notify?threadId=` | SSE, AG-UI event JSON, not a run | `IUserFrontend` callbacks |
| `GET /threads/{id}/messages` | JSON (`MESSAGES_SNAPSHOT` shape) | `GetHistoryAsync` |
| `POST /threads/{id}/cancel` | REST | `CancelSessionAsync` |
| `POST /threads/{id}/clear` | REST | `ClearContextAsync` |
| `POST /questions/{id}/answer` | REST | `SendMcpAnswerAsync` |
| `POST /feedback` | REST | `SendFeedbackAsync` |
| `POST /attachments`, `GET /attachments/{*path}` | REST | `UploadAttachmentAsync`, shared-volume read (as Blazor's `/attachments`) |
| `GET/POST /mcp-signin…` | REST + OAuth callback | `ListMcpSignInsAsync`, `StartMcpSignInAsync`, `SendMcpSignInCallbackAsync` |
| `GET/POST/DELETE /saved…` | REST | saved-response methods |
| `GET /agent` | REST | `GetAgentInfoAsync`, `GetActiveStatusAsync` |
| `GET /healthz` | anonymous | — |

### A run

`POST /agui` with `RunAgentInput`:

1. **Validate.** Check `threadId` against the allowed sessions (see *Sessions*). Take the last user
   message from `messages`; the agent owns history, so earlier messages are ignored. Read
   capabilities and attachments from `forwardedProps.rockbot`.
2. **Start the stream.** Emit `RUN_STARTED { threadId, runId }`.
3. **Send.** Call `UserProxyService.SendAsync(UserMessage, progress, timeout, ct)` with
   `SessionId = threadId`, `UserId` from the caller's identity, `ChannelName = "agui"` and the
   declared `ClientCapabilities`.
4. **Progress.** Each `IProgress<AgentReply>` report (correlated tool progress such as
   `Calling web_search…`) becomes an `ACTIVITY_DELTA` on one activity with `activityType:
   "rockbot.progress"`, appending the line. Clients that ignore activity events lose nothing but
   the spinner text.
5. **Final reply.** Emit `TEXT_MESSAGE_START` (role `assistant`, `messageId` = a new id),
   one `TEXT_MESSAGE_CONTENT` with the whole `Content`, and `TEXT_MESSAGE_END`. Attachments go in a
   `CUSTOM rockbot.attachments` event that references `/attachments/...` URLs.
6. **Finish.** Emit `RUN_FINISHED`, with an interrupt outcome when MCP questions are open (see
   below).

**Cancellation.** If the client aborts the request, the proxy calls `CancelSessionAsync(threadId)`.
The agent's cancel acknowledgement isn't streamed, because nobody is listening. `POST
/threads/{id}/cancel` does the same thing from another connection. The open run then ends with
`RUN_FINISHED` outcome `cancelled`.

**Long turns.** `SendAsync` gives up after `DefaultReplyTimeout` (3 min by default). The agent
keeps working, and its reply then arrives uncorrelated, so it's delivered through `IUserFrontend`
like any unsolicited reply. The proxy uses a long run timeout (configurable, default 30 min) and
sends an SSE comment every 15 s so proxies and mobile networks don't drop an idle stream. If the
timeout still hits, the run ends normally with a `CUSTOM rockbot.reply_pending` event, and the
reply arrives later on the notify stream. This is the same behaviour the Blazor UI shows as "No
reply received (timeout)", followed by a late bubble.

**Errors.** Bus or agent failures become `RUN_ERROR`.

### Unsolicited output: the notify stream

AG-UI can't carry output that no run asked for, so the proxy offers its own SSE endpoint next to
it. To keep clients to one parser, each event on it is an AG-UI event object, but the stream isn't
a run: it has no `RUN_STARTED`, and it never finishes on its own.

| `IUserFrontend` callback | Notify event |
|---|---|
| `DisplayReplyAsync` (final, unsolicited) | `TEXT_MESSAGE_START` / `CONTENT` / `END`, plus `CUSTOM rockbot.origin` carrying `ReplyOrigin` |
| `DisplayReplyAsync` with an `McpQuestionForm` | `CUSTOM rockbot.mcp_question` (the form) |
| `DisplayStatusAsync` (subagent, worker, A2A progress) | `ACTIVITY_SNAPSHOT` / `DELTA` keyed by `AgentName`; `IsCompletion` closes it |
| `OnMcpQuestionStatusAsync` | `CUSTOM rockbot.mcp_question_status` |
| `OnMcpSignInStatusAsync` | `CUSTOM rockbot.mcp_signin_status` |
| `OnAgentNameChangedAsync` | `CUSTOM rockbot.agent_name` |
| `DisplayErrorAsync` | `CUSTOM rockbot.error` |

**Routing.** The Blazor frontend dumps everything into one `ChatStateService`. This one keeps a
registry of notify subscribers keyed by `threadId`, and fans each callback out to the subscribers
of the reply's `SessionId`. Scheduled-task sessions (`scheduled`, `WellKnownSessions.ScheduledSystem`)
and `a2a-inbound` go to every subscriber, which is what Blazor shows today.

**Missed events.** Each event gets a per-thread sequence id, used as the SSE `id`. The proxy keeps a
bounded ring buffer per thread (default 200 events or 24 h), so a reconnect with `Last-Event-ID`
replays what was missed. If the gap is older than the buffer, the proxy sends `CUSTOM
rockbot.resync`, and the client refetches `GET /threads/{id}/messages`.

**Mobile push**, through Apple's or Google's push services, would be a later addition on the same
routing: a phone app wants scheduled-task output while it's closed, and no SSE stream survives
that. It's out of scope here.

### MCP questions

How they work today:

- **Delivery.** The bridge publishes a handed-back question as an `AgentReply` with `ContentType =
  McpQuestionForm.ContentType` on the **broadcast** topic. It's uncorrelated, has `IsFinal = true`,
  and **doesn't end the turn**.
- **Capability.** Forms go only to sessions that advertised `InteractiveForms`; other clients get
  the question relayed as text by the agent.
- **Answer.** An `McpQuestionAnswer`. The agent's answer reply carries `FollowsQuestionId`.
- **Chatting continues.** The user can keep chatting while a question is open.

AG-UI ties questions to the end of a run and blocks new input until they're resolved. The proxy
offers both models, chosen per request in `forwardedProps.rockbot.questionMode`:

- **`interrupt` (default; spec-conformant).** Generic AG-UI clients get standard human-in-the-loop.
  - **During the run.** A form that arrives on the open run's thread is held. When the final reply
    comes, `RUN_FINISHED` carries one interrupt per open question:
    - `id` = `QuestionId`;
    - `reason` = `input_required` (`confirmation` when the form is a single `IsDecision` boolean);
    - `message`, `expiresAt`;
    - `responseSchema` rebuilt from `Fields`.
  - **Outside a run.** A form that arrives with no run open goes out on the notify stream, and the
    next `POST /agui` on that thread must resume it.
  - **Resume.** A run with `resume` sends one `McpQuestionAnswer` per entry: `resolved` carries
    `payload` as `Answers`, and `cancelled` sets `Decline = true`. The run then streams the replies
    whose `FollowsQuestionId` match, and finishes once each question is settled (answered,
    declined, expired, rejected) and its follow-up reply has arrived.
  - **Blocking.** Following the spec, a run with new input and no `resume` while interrupts are open
    gets `RUN_ERROR`.
- **`custom` (RockBot's own clients).** Matches the Blazor UI. Questions arrive as `CUSTOM
  rockbot.mcp_question` events, in the run if one is open or on the notify stream otherwise. They're
  answered with `POST /questions/{id}/answer`, and new input is never blocked.

`rejected` (schema failure, question still open) maps to `RUN_ERROR` on the resume run in interrupt
mode, so the client can resubmit; in custom mode it's an `rockbot.mcp_question_status` event, as in
Blazor.

### Capabilities

AG-UI has no capability negotiation, so the client declares its own in
`forwardedProps.rockbot.capabilities`, as an array of `ClientCapabilities` names (strings rather
than the numeric bitfield, so a client in another language can write it). A new preset,
`ClientCapabilityPresets.AgUi`, is the default when none are declared: text, all Markdown subsets,
inline links and image attachments.

That default deliberately leaves out two capabilities:

- **No `HtmlInline` / `SvgInline`**, because a generic client may not sanitize them.
- **No `InteractiveForms`**, so a client that can't render forms gets questions as text, exactly as
  the CLI does.

A client that declares `InteractiveForms` receives forms in either question mode.

### Sessions and identity

`threadId` becomes `SessionId`. A configured list says which thread ids clients may use: by
default a single `agui-session`, the way Blazor pins `blazor-session`. That keeps the agent's
session count, history and working-memory namespacing where they are today. A client that sends an
unlisted thread id gets HTTP 403. `UserId` comes from the authenticated principal, or a configured
default when auth is off.

### Auth

There are two modes, as for Blazor:

- **Network.** Tailnet only, no app auth (the default; the chart refuses a public ingress without
  auth).
- **App.** Bearer tokens. The proxy validates OIDC ID or access tokens (Google first, matching
  Blazor), and the same email/domain allowlist applies. The `UserAllowlist` and options move from
  `RockBot.UserProxy.Blazor/Auth` into a shared library so both proxies use one implementation.

With CORS, only the configured WASM origins may call the API.

The MCP sign-in callback needs a browser redirect URI. It becomes `https://<agui host>/mcp-oauth/callback`,
which the agent's OAuth client registrations must accept, as `docs/blazor-ui.md` describes for Blazor.

### Scaling

Each replica has its own `ProxyId`, so it gets:

- its own correlated replies, delivered point-to-point to the replica holding the run;
- its own copy of every broadcast.

Runs therefore work on any replica. The catch is the notify buffer: it's per replica, so
`Last-Event-ID` replay only works when the client reconnects to the same replica. v1 runs one
replica. Sticky sessions or a shared buffer can come later if needed.

## Later: richer bus events

The proxy is only as rich as the bus. Today the bus carries whole replies and free-text progress,
so the proxy emits text chunks and activity. Each of these agent-side changes lets every proxy,
Blazor included, show more:

1. **Streaming text deltas.** Gives `TEXT_MESSAGE_CONTENT` per chunk.
2. **Structured tool progress.** Tool name, call id, arguments and result summary give real
   `TOOL_CALL_START` / `ARGS` / `END` / `RESULT`.
3. **Subagent lifecycle.** Started and finished events, with the spawning tool call, give
   `SUBAGENT_STARTED` / `FINISHED` and `subagentRunId` attribution.
4. **Reasoning summaries.** Give `REASONING_*`.

When those land on the bus, model their shapes on the matching AG-UI events, so this proxy's mapping
stays mechanical.

## Phases

One PR each.

1. **Runs.** Project, hosting, `POST /agui` (run, progress, final text, cancel, errors), `GET
   /threads/{id}/messages`, network-mode auth, Helm chart, Dockerfile. *Implemented.*
2. **Notify stream.** Routing frontend, ring buffer, `Last-Event-ID`, resync. *Implemented.*
3. **MCP questions.** Both modes, with `responseSchema` built from `McpQuestionField`.
4. **REST extras.** Attachments, feedback, saved responses, MCP sign-in, clear context, agent info.
5. **App auth.** Bearer tokens, the shared allowlist, CORS.
6. **A reference client.** Probably a small Blazor WASM app on `AGUIChatClient` and the notify
   stream; it can live in this repo as a sample.

## Implementation status

### Phase 1: runs (implemented)

- **`src/RockBot.UserProxy.AgUi.Events`** is the shared library. It holds `TurnEventMapper` (moved
  from the CLI), `TurnEventEmitter` (the ordering rules that were in the CLI's
  `JsonLinesTurnWriter`: `RUN_STARTED` first, nothing after the terminal event) and `AgUiJson`. The
  CLI's `--output jsonl` and the proxy emit events from the same code.
- **`src/RockBot.UserProxy.AgUi`** is the proxy: `POST /agui`, `POST /threads/{id}/cancel`,
  `GET /threads/{id}/messages` and `GET /healthz`, network-mode auth only.
- **`ClientCapabilityPresets.AgUi`** is the default capability set.
- **Deployment:** `src/RockBot.UserProxy.AgUi/Dockerfile` and Helm values `agui.*` (off by default).
  There are no `agui.auth` or `agui.cors` values until phase 5, so the chart refuses a public
  ingress unless `agui.ingress.allowUnauthenticated` is set.

Where the implementation differs from the design above:

1. **Timeouts.** The design's long run timeout followed by `CUSTOM rockbot.reply_pending` assumed
   `SendAsync`. The proxy uses `SendTurnAsync` (#795), so a run lives for as long as the agent sends
   heartbeats, up to `MaxTurnDuration`. In phase 1 a run that was lost or capped ended with
   `RUN_ERROR` (`agent_lost` or `timeout`), as it does in `rockbot chat --output jsonl`. Phase 2
   changed the timed-out case to `reply_pending`; see below.
2. **Ephemeral queues.** The design says `EphemeralQueues: false`. A Deployment's pod name, and
   with it `ProxyId`, changes on every rollout, so durable queues would pile up on the broker. The
   proxy sets `EphemeralQueues = true`. Nothing is lost: a new pod has a new id and would never read
   the old queues.
3. **Progress.** Progress is `ACTIVITY_SNAPSHOT` (each one replaces the last) and real `TOOL_CALL_*`
   events, not `ACTIVITY_DELTA`. That is what the shared mapper already emitted for the CLI. Task
   lists are `STATE_SNAPSHOT` / `STATE_DELTA`.
4. **Refusals before the stream.** Problems the proxy can see up front are plain HTTP errors, so a
   client never gets a stream that starts and immediately fails: 400 (malformed input, no user
   message, or `resume`, which waits for phase 3), 403 (thread not allowed), 409 (a run with the
   same `runId` is in progress; the `runId` is the bus correlation id) and 503 (not connected to the
   bus).
5. **Shutdown.** A proxy that is stopping ends its open runs with `RUN_ERROR` code `proxy_stopping`
   and does not cancel the session. Only a client abort cancels.
6. **Runs carry only their own traffic.** A run's stream has the turn's correlated traffic and
   nothing else. Subagent lifecycle events for the run's session, which the CLI does put in its
   stream, are not in it; since phase 2 they are on the notify stream. Because nothing can arrive
   late, the proxy doesn't use the CLI's settle wait before the terminal event.
7. **SSE is written directly.** The proxy writes `data: {json}` frames itself and references only
   `AGUI.Abstractions`, not `AGUI.Formatting`. `AGUI.Client` is a test-only reference.
8. **Multimodal input.** A user message's image and document parts are dropped; only its text is
   sent. Attachments come with phase 4.

Open after phase 1: aborting a request cancels **all** in-flight work for the thread, and by default
every client shares one thread. A phone that drops its connection mid-run therefore cancels the turn.
That follows this design and the AG-UI convention. If it proves too aggressive for mobile, an
opt-out (say `AgUi:CancelOnDisconnect`) is a small follow-up.

### Phase 2: the notify stream (implemented)

- **`GET /agui/notify?threadId=`** serves the thread's unsolicited output as server-sent events.
  Each has an `id` and one AG-UI event as `data`, written by the same serializer as a run's.
- **`AgUiUserFrontend`** is now the routing frontend: every `IUserFrontend` callback is mapped to
  events and published to the threads it concerns.
- **`NotifyHub`** holds, for each allowed thread, the sequence, the ring buffer and the subscribers.
- **`SessionRouting`** (shared library) holds the session-matching rule that was in the CLI's
  `JsonLinesFrontend`, and the list of sessions that go to every thread.
- **Long turns.** A run that ends as `TimedOut` now ends with `CUSTOM rockbot.reply_pending` and a
  plain `RUN_FINISHED`. `agent_lost` is still `RUN_ERROR`. The CLI is unchanged: it has no notify
  stream, so it still reports `RUN_ERROR` with code `timeout`.

Decisions:

1. **AG-UI event JSON on the notify stream** (open question 1). One parser serves a run and the
   notify stream. What AG-UI has no event for is `CUSTOM rockbot.*`.
2. **Event ids are `{epoch}-{sequence}`.** The sequence counts up from 1 for each thread. The epoch
   is random for each proxy process. A bare number would collide after a restart: the new process
   also has an event 1, and a client's old `Last-Event-ID` would be taken for a position in the new
   sequence, silently skipping or repeating events. With the epoch, an id from another process is
   recognised and answered with `rockbot.resync` (`reason: "unknown"`); an id older than the buffer
   gets `reason: "expired"`. Clients treat the id as opaque.
3. **`rockbot.connected`.** A connection without `Last-Event-ID` starts with this event, whose id is
   the stream's position. Without it a client has no id until the first real event, and a drop
   before then would lose whatever happened in between. It and `rockbot.resync` take no sequence
   number: they belong to one connection, not to the thread's history.
4. **No gap, no duplicate.** Subscribing computes the replay and registers the subscriber under the
   lock that numbers events, so every event is either in the replay or delivered live, never both
   or neither.
5. **Slow clients are dropped.** Each subscriber has a bounded queue (256 events, and never less
   than a whole buffer, which a reconnect is handed at once). The bus's thread only ever does a
   non-blocking write to it. When it is full the subscriber is removed and its stream ended; the
   client reconnects with `Last-Event-ID` and replays. The alternative, discarding events for that
   client, would leave a gap it can't detect.
6. **A subagent's completion is its result.** `DisplayStatusAsync` with `IsCompletion` is what
   `SubagentResultHandler` sends when a subagent finishes: the subagent's whole output, non-final.
   The Blazor UI adds it to the subagent's activity log and closes the log; the CLI emits it as a
   message. The notify stream does both, because both are true of what the agent sends: an
   `ACTIVITY_SNAPSHOT` with `content.done` closes the activity (the design's table), and the output
   follows as a text message with `subagentRunId` and `rockbot.origin` (it is a result, often long,
   not a status line). The agent's own synthesis arrives later as a separate final reply.
7. **Task lists are always snapshots.** The run mapper sends `STATE_DELTA` patches. On a stream a
   client can join at any event, a patch may refer to a list the client never received, so the
   notify stream sends the whole `STATE_SNAPSHOT` each time. A finished subagent's list is removed,
   so the state doesn't grow for as long as the proxy runs.
8. **Runs stay correlated-only.** Broadcast events are not copied into an open run. Nothing a
   run-only client needs is lost by that today: a run's reply, progress, tool calls and task list
   are all correlated. What a run-only client doesn't see is what it never could (unsolicited
   output), plus MCP question forms, which the bridge broadcasts; those are phase 3's subject.
9. **More callbacks than the table lists.** Besides the table's rows, the frontend maps subagent
   lifecycle (`SUBAGENT_*`), subagent task lists (`STATE_SNAPSHOT`), run events for a turn the proxy
   stopped waiting for (`rockbot.agent_run_started`, `rockbot.heartbeat`, `rockbot.run_finished`,
   each with its `runId`) and the agent's active-work broadcast (`rockbot.active_work`).

Where the implementation differs from the design above:

1. **Progress is `ACTIVITY_SNAPSHOT` and `TOOL_CALL_*`**, not `ACTIVITY_DELTA`, as in runs. The
   activity is keyed by the subagent's or worker's task id when the reply has one, else by
   `AgentName`.
2. **`a2a-inbound/<context id>`** sessions are treated like `a2a-inbound`. The Blazor UI matches the
   bare prefix `a2a-inbound`; the proxy requires the exact id or the `/` form, so an allowed thread
   that merely starts with those letters is still its own thread.
3. **Only allowed threads have a stream.** Traffic for any other session (`blazor-session`, a CLI
   session) is dropped, where the Blazor UI shows everything it hears.
4. **A dropped client's connection may be reset.** The proxy stops in the middle of a write, so the
   stream doesn't end cleanly. The client treats it like any other drop.

Open after phase 2:

- **A late reply doesn't say which run it answers.** `IUserFrontend.DisplayReplyAsync` gets the
  reply without its correlation id, so the text message on the notify stream can't carry the
  `runId` of the run that ended with `reply_pending`. A client can pair them by order, or by the
  `rockbot.run_finished` event that follows. Passing the correlation id through the frontend
  interface would fix it.
- **A narrow race in `UserProxyService`.** A reply that arrives after a turn has timed out but
  before the turn is removed from the pending table completes a turn nobody is reading, and is
  lost. The Blazor UI has the same window. It is a few instructions wide.
- **Notify state and run state are separate documents.** Both use `STATE_SNAPSHOT`. A client has to
  keep them apart (see `docs/agui.md`).

## Testing

**Shared with the CLI.** `rockbot chat --output jsonl` (#797, [`docs/cli.md`](../docs/cli.md))
writes one turn's bus traffic as AG-UI events built with `AGUI.Abstractions` 1.0.1: the run lifecycle,
progress as activity, tool calls, task lists as state, subagent lifecycle, the reply, and attachments
and origins as `CUSTOM rockbot.*` events. The mapping is `TurnEventMapper`, now in
`src/RockBot.UserProxy.AgUi.Events`, and the proxy uses the same one.

**Phase 1 tests** are in `tests/RockBot.UserProxy.AgUi.Tests`: the request and history mappings as
pure functions, the endpoints through `WebApplicationFactory` with the in-process bus and a fake
agent, and a conformance test that drives `POST /agui` with `AGUIChatClient`.

**Phase 2 tests** are in the same project: the hub (ordering, both bounds, replay, resync, dropped
subscribers, and publishers racing reconnecting subscribers) and the routing as pure tests; the
notify endpoint through the test host, with the fake agent publishing on the broadcast topics; and
one test over a real socket for a client that stops reading.

- **Unit.** The `AgentReply` → event mapping and question-mode state are pure functions over
  recorded envelopes. Use `RockBot.Messaging.InProcess` for the proxy service.
- **Conformance.** Drive `POST /agui` with `AGUI.Client`'s `AGUIChatClient` in an integration test,
  and check that its parser accepts every stream the proxy emits, interrupts and resume included.
- **Live.** Run against the cluster agent, as with the CLI (`reference_rockbot_cli`), plus one
  third-party AG-UI client to check interoperability.

## Open questions

1. **The notify stream's shape.** *Decided: AG-UI event JSON.* Is reusing AG-UI event JSON outside
   a run the right call, or does it confuse clients that validate streams strictly? The alternative
   is plain RockBot JSON (`AgentReply` and friends), which is simpler and honest about not being
   AG-UI.
2. **Interrupt as default.** Should `interrupt` be the default question mode? It's what generic
   clients expect, but blocking new input is a real UX regression compared with Blazor.
3. **What history replays.** Does conversation history include unsolicited replies (subagent
   results, scheduled tasks) with enough metadata to rebuild `ReplyOrigin` on reload? If not,
   `rockbot.resync` loses anchors.

   *Answered (2026-11-14, from the agent's code): some of the replies, and none of the origin.*
   `ConversationHistoryRequestHandler` returns the session's `ConversationTurn`s, which have a role,
   content, timestamp, `AgentName` and attachments. There is no origin, no `ReplySource` and no
   message id.
   - **Replies in the thread's own session are there.** A subagent's result is stored as a
     synthetic *user* turn (`[Subagent task <id> completed]: …`) with `AgentName` `subagent-<id>`,
     and the agent's synthesis as an assistant turn (`SubagentResultHandler`). A2A results and MCP
     task results are stored the same way. The kind of source can be guessed from the `AgentName`
     prefix, as `ChatReducer.CategorizeHistoryTurn` does; `AgUiHistoryMapper` doesn't do that yet,
     so those synthetic turns come back as user messages.
   - **`ReplyOrigin` can't be rebuilt.** Its channel, prompt summary and start time live in the
     agent's `SessionOriginStore` and travel only on the live reply.
   - **Broadcast-session output isn't there at all.** A scheduled task replies in the `scheduled`
     or `scheduled-system` session and writes nothing to the thread's conversation memory, and
     inbound A2A notifications are in `a2a-inbound`. A user's scheduled-task reports are kept in
     the saved-response archive (#418), which phase 4's `/saved` endpoints will expose.

   So `rockbot.resync` does lose anchors, and loses scheduled and inbound A2A output outright. The
   buffer (200 events, 24 hours) is what keeps that rare. Closing the gap needs an agent-side change:
   `Origin` and `Source` on `ConversationTurn` (additive under `design/schema-migrations.md`), and
   either history for the broadcast sessions or a client that reads the saved-response archive
   after a resync.
4. **One thread or many.** Should the proxy allow more than one thread id (say, one per device)?
   That's cheap for the proxy, but every extra session splits history and working memory on the
   agent side.
5. **Protobuf.** It's skipped for now (SSE is mandatory; protobuf is optional and covers a subset of
   events in .NET). Revisit only if a mobile client needs it.
