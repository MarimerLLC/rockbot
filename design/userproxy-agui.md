# AG-UI User Proxy

**Status:** Phases 1 (runs), 2 (notify stream), 3 (MCP questions), 4 (REST extras) and 5 (app
auth) implemented, 2026-11-14. Phase 6 is not started. See
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
  data-protection key ring if auth needs one. (It doesn't; see
  [Phase 5](#phase-5-app-auth-implemented).)

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

How the capability and the mode interact, which mode governs a form that arrives between requests,
and what a proxy restart does to open questions were left open here; see
[Phase 3](#phase-3-mcp-questions-implemented).

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
   *Implemented.*
4. **REST extras.** Attachments, feedback, saved responses, MCP sign-in, clear context, agent info.
   *Implemented.*
5. **App auth.** Bearer tokens, the shared allowlist, CORS. *Implemented.*
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
   sent. Files go with a message as references to uploads (phase 4).

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

MCP question forms, which were only events on the notify stream in phase 2, are phase 3's subject
(below).

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

### Phase 3: MCP questions (implemented)

- **`McpQuestionRegistry`** is the proxy's question state: for each allowed thread, the questions
  shown as forms and not yet settled, and the runs open on it. It decides where a form goes,
  whether new input is blocked, what a `resume` must cover, and which run gets the reply to an
  answer. `AgUiUserFrontend` feeds it forms, statuses and follow-up replies.
- **`McpQuestionInterrupts`** turns an `McpQuestionForm` into an `AGUIInterrupt` (`id`, `reason`,
  `message`, `expiresAt`, a `responseSchema` built from the fields, and the form in
  `metadata.rockbot`) and builds the `rockbot.mcp_question` event.
- **`ResumeRun`** is a run with `resume`: one `McpQuestionAnswer` per entry, then the statuses and
  follow-up replies of those questions, streamed until each is settled.
- **`POST /questions/{id}/answer`** answers or declines a question over REST.
- **`TurnEventMapper.Finish` / `ReplyPending`** (shared library) take the interrupts a run ends
  with. `rockbot.origin` carries `followsQuestionId`.

Decisions:

1. **`interrupt` is the default mode** (open question 2), for a client that takes forms at all.
2. **Forms are opt-in, and naming a mode opts in.** The agent only sends forms to a session that
   declared `InteractiveForms`, which the default capabilities leave out. A request gets forms by
   listing `InteractiveForms` (mode `interrupt` unless it says otherwise) or by setting
   `questionMode`, which adds `InteractiveForms` to its capabilities. A request that does neither
   has no question mode: questions come as text, it never ends with an interrupt, and it is never
   blocked. That keeps a generic AG-UI client that doesn't implement interrupts (they are new in
   1.0) from being locked out of a thread by an interrupt it can't answer, and keeps a client
   without forms from being blocked by another client's question on a shared thread.
3. **The mode belongs to the request; there is no thread-level mode.** A form that arrives while a
   run is open is handled by that run in its mode; one that arrives with no run open goes to the
   notify stream; the next request's own mode decides whether it is blocked. `rockbot.mcp_question`
   is the same event everywhere and carries both the form and the interrupt, so no state is needed
   to say which a client wants. Either way of answering works for any question.
4. **Blocked input is `RUN_ERROR`, not an HTTP error.** Phase 1 refuses what it can see in the
   request with HTTP. An open interrupt is a state of the conversation that the spec expects a
   client to handle: a generic client shows a run's error to its user, where it may treat an HTTP
   failure as a transport problem and retry. So blocked input (`interrupts_pending`) and a `resume`
   that leaves an interrupt out (`resume_incomplete`) are runs that fail. Before the error the run
   lists the open questions as `rockbot.mcp_question` events, so a client that lost them (a reload)
   can still answer and is not locked out until they expire. A *malformed* `resume` is still `400`,
   and two runs resuming one interrupt is `409`, like a duplicate run id.
5. **A run carries all of the thread's open questions**, not only those that arrived during it.
   In practice they are the same, since an interrupt-mode run can't start while questions are open.
   A run that ends with `rockbot.reply_pending` carries them too. A run that fails or is cancelled
   has no `RUN_FINISHED` to carry them, so the questions it held go to the notify stream.
6. **A resume run's end.** Per answered question: `rejected` ends the run with `RUN_ERROR`
   (`interrupt_rejected`); `answered` or `declined` by the user waits for the reply with
   `FollowsQuestionId`; any other state (`unavailable`, `expired`, `cancelled`, `interrupted`, or
   answered by the agent's own `mcp_answer`) settles it with no reply. Two bounds: no status within
   `UserProxy:ReplyTimeout` is `RUN_ERROR` `agent_lost` (the interrupt stays open); no reply within
   `AgUi:ResumeReplyTimeout` (5 minutes) ends the run with `rockbot.reply_pending`, and the reply
   then goes to the notify stream.
7. **Aborting a resume run cancels nothing.** The answers are already sent, and cancelling the
   session would stop the work they released. A run with new input still cancels on abort.
8. **No duplicates.** A reply with `FollowsQuestionId` goes to the run that resumed the question
   if it still waits for it, else to the notify stream, decided under the lock that ends a run. A
   form is shown in a run or on the notify stream, never both. Statuses are the exception: always
   on the notify stream, because every client that shows the form has to close it, and also in the
   resume run.
9. **Nothing is persisted.** The agent owns a question; the registry is a cache built from the
   bus. After a restart it is empty: new input is not blocked, and an answer to a question the
   proxy doesn't know (a `resume` entry or the answer endpoint) is passed to the agent, which
   reports `unavailable` if it doesn't exist. A question also drops out of the registry at its
   `ExpiresAt`, so a missed status can't block a thread for good.

Where the implementation differs from the design above:

1. **A resume run ignores `messages`.** The design doesn't say. A client replays the conversation
   with every request, so a user message there says nothing about new input.
2. **`resume` may name a question the proxy doesn't know** (decision 9). "Must address every open
   interrupt" is checked against what the proxy knows to be open.
3. **The `rockbot.mcp_question` event has an `interrupt` property**, in both modes.
4. **Expired and unavailable questions end a resume run normally**, with their status event. The
   design lists them with `rejected` as ways a question settles; only `rejected` is an error.

Open after phase 3:

- **An answer with no reply can't be told from a slow one.** When an answer goes on to a background
  task or a waiting subagent, `McpQuestionAnswerHandler` publishes nothing more, so a resume run
  waits out `AgUi:ResumeReplyTimeout`. A status or flag from the agent ("no reply follows") would
  let it end at once.
- **A new notify connection isn't told the open questions.** It gets them by replay
  (`Last-Event-ID`) or, in interrupt mode, in the refused run. A client that reloads in custom mode
  and is beyond the buffer doesn't see a still-open form. *Closed in phase 4:*
  `GET /threads/{id}/questions`.
- **Shared threads.** The agent keeps one capability set per session, from the last message. A
  client without forms and one with forms on the same thread flip it back and forth.
- **Replicas.** Each replica routes follow-up replies by its own runs, so with more than one the
  reply is in the resume run on one replica and on the others' notify streams.
- **Progress of the agent's follow-up turn** (its tool calls after an answer) is uncorrelated and
  goes to the notify stream, not into the resume run.

### Phase 4: REST extras (implemented)

- **`AgUiRestEndpoints`** holds the endpoints; each is one call on `UserProxyService`:

  | Endpoint | Maps to |
  |---|---|
  | `POST /attachments` | `UploadAttachmentAsync` |
  | `GET /attachments/{**path}` | a read from the shared volume |
  | `POST /feedback` | `SendFeedbackAsync` |
  | `GET /saved`, `GET /saved/{id}`, `POST /saved`, `DELETE /saved/{id}` | the four saved-response methods |
  | `GET /mcp-signin`, `POST /mcp-signin/{server}/start` | `ListMcpSignInsAsync`, `StartMcpSignInAsync` |
  | `GET /mcp-oauth/callback` | `SendMcpSignInCallbackAsync` |
  | `POST /threads/{id}/clear` | `ClearContextAsync` |
  | `GET /threads/{id}/questions` | `McpQuestionRegistry.Open` |
  | `GET /agent` | `GetAgentInfoAsync`, `GetActiveStatusAsync` |

- **`forwardedProps.rockbot.attachments`** on a run puts upload references on the `UserMessage`.
- **`AttachmentPathResolver` and `AttachmentPresentation`** moved from the Blazor project to
  `RockBot.UserProxy`, which both proxies reference, so the containment check and the response
  headers for untrusted files are one implementation. Their tests moved with them.
- **`McpSignInTracker`** remembers the sign-ins the proxy started, by OAuth `state`.
- **`rockbot.attachments`** entries carry a `url` when the host serves attachments
  (`TurnEventMapper.AttachmentUrl`). The CLI doesn't set it, so its JSON lines are unchanged.
- **`rockbot.context_cleared`** is a new notify event.
- **Deployment:** the pod mounts the shared PVC read-only and gets `ROCKBOT_SHARED_PATH`, and
  `UserProxy__AttachmentUploadUrl` when the agent's upload endpoint is on. New value:
  `agui.publicBaseUrl`.

Decisions:

1. **One group for phase 5.** `MapAgUi` maps every endpoint a client calls on one route group;
   app auth is a single `RequireAuthorization()` there. `/healthz` and `/mcp-oauth/callback` are
   mapped outside it and marked anonymous.
2. **Attachments are served by path, not by query.** `GET /attachments/previews/ab12/deck.html`,
   where the Blazor UI has `/attachments?file=…`. An HTML attachment's relative links (a deck's
   images and scripts beside it) then resolve to their own attachment URLs, which a query can't
   give, and the event's `url` is an ordinary path a client can hand to `<img>`. The containment
   check is the same resolver either way.
3. **Served files are untrusted, as in Blazor:** `nosniff` on every response, `sandbox
   allow-scripts` for HTML and `sandbox` for SVG. The proxy has no cookies for such a document to
   steal, but a WASM client may be served from the same origin, and phase 5 may add them.
4. **The proxy doesn't write files.** An upload goes to the agent (over HTTP when
   `UserProxy:AttachmentUploadUrl` is set, else over the bus), which owns the volume, the stored
   name and the list of accepted types. The proxy refuses what it can without the agent: over 8 MB
   (`AttachmentUploadRequest.MaxBytes`, now the one constant the agent and both proxies use), and
   a declared type that isn't the type `AttachmentMimeTypes` gives the file's extension. The
   agent's own, narrower allowlist is `422`.
5. **Upload takes multipart or a raw body.** Multipart is what a browser's `FormData` and
   `MultipartFormDataContent` produce; a raw body with `Content-Type` and `?fileName=` is the
   least a small client has to do.
6. **An attachment reference is held to the form an upload produces.** A client hands back
   `{mime, path, fileName}`, so it can name any path. Three checks before the message is sent: the
   path is a plain relative one (`AttachmentPathResolver.IsSafeRelativePath`: lexical, the same on
   every platform, so a Linux proxy refuses a backslash path a Windows agent would misread); the
   type is the type of the path's extension; and, where the proxy has the volume, the file exists
   under the attachments directory. The agent still resolves the path with its own containment
   check. The proxy does not track which references it issued: that state would be lost on a
   restart and absent on another replica, and referencing a file already in the directory (one the
   agent attached earlier) is legitimate.
7. **The sign-in redirect URI is the proxy's own, always.** A client can't supply one, since the
   authorization code is sent wherever it points. It is built from `AgUi:PublicBaseUrl`, or from
   the request when that is empty. Blazor's `Auth:PublicBaseUrl` rewrites every request and has a
   forwarded-headers option; the proxy builds one URL, so it has neither.
8. **The callback only relays what the proxy started.** Blazor's callback relays any `state` and
   leaves the matching to the bridge, behind the app's own sign-in. This one will be anonymous for
   good (a browser arriving from an authorization server has no bearer token), so it relays a
   redirect only when its `state` is one of the sign-ins the proxy started, unexpired, and once.
   A sign-in started before a proxy restart, or on another replica, therefore has to be started
   again. A redirect with a known state and no code is still relayed, so the bridge fails the
   sign-in at once instead of letting it expire.
9. **Clearing the context tells every client.** The agent clears, stops the session's work and
   confirms with an unsolicited reply. The proxy adds `rockbot.context_cleared` on the thread's
   notify stream, published before the request goes to the agent so the confirmation follows it.
   The proxy's own state is left alone: the notify buffer keeps its events (a reconnecting client
   replays them in order, the clear included); open questions stay until the agent closes them or
   they expire, since they belong to MCP calls; an open run ends as cancelled when the agent
   reports it.
10. **A shorter wait for REST.** `AgUi:RequestTimeout` (30 seconds) instead of
    `UserProxy:ReplyTimeout` (3 minutes), which is sized for a turn. Starting a sign-in waits 90
    seconds, as the Blazor UI does.

Where the implementation differs from the design above:

1. **`GET /threads/{id}/questions`** is not in the design's table. It closes a gap phase 3 left.
2. **`rockbot.attachments` references by `url` and by `path`.** The design says the event
   "references `/attachments/...` URLs"; it keeps the attachment as the bus has it and adds `url`.
3. **The callback page and its HTML are a second copy** of the Blazor UI's (25 lines). They differ
   in the state check, and sharing them would put ASP.NET Core in `RockBot.UserProxy`.

Open after phase 4:

- **Attachments and bearer tokens (phase 5).** A browser can't send an `Authorization` header from
  `<img src>` or a new tab. With app auth, `GET /attachments/…` needs another way in for those
  uses: a cookie, a short-lived signed URL, or a client that fetches the bytes and shows a blob.
  *Closed in phase 5:* access tickets.
- **The accepted upload types are stated twice.** The agent's `InboundAttachmentService` has the
  allowlist; the proxy only knows the wider `AttachmentMimeTypes` table, so an SVG or HTML upload
  travels to the agent to be refused. Moving the allowlist to `RockBot.UserProxy.Abstractions`
  would let every client refuse up front.
- **Multimodal input.** Image and document parts of an AG-UI message are still dropped. Mapping
  them (upload, then reference) would let a generic AG-UI client send files without knowing
  RockBot.
- **Synthetic history turns.** `AgUiHistoryMapper` still shows a subagent's stored result as a
  user message (open question 3).
- **Replicas.** The sign-ins under way are per replica, like the notify buffer and the question
  list.

### Phase 5: app auth (implemented)

- **`AgUi:Auth`** (`AgUiAuthOptions`) turns app mode on. Off, no auth services are registered and
  no auth middleware is added.
- **`OriginCheck`** refuses a request to a client endpoint from an origin that isn't allowed, in
  both modes.
- **Bearer tokens** are validated by ASP.NET Core's JWT bearer handler against one OIDC issuer
  (`Authority`, Google by default) and a list of audiences.
- **`UserAllowlist`** and a new base class for its two lists, `UserAllowlistOptions`, moved from
  the Blazor project to `RockBot.UserProxy`. Blazor's `AuthOptions` and the proxy's
  `AgUiAuthOptions` both derive from the base class. Their tests moved with them.
- **`AccessTicketService`, `AccessTicketHandler`, `POST /tickets`** and
  `GET /t/{ticket}/attachments/{path}`: short-lived signed URLs for the requests a browser makes
  without a script.
- **`AgUi:Cors:AllowedOrigins`** (`AgUiCorsOptions`) adds ASP.NET Core's CORS middleware when it
  lists any origin.
- **`AgUiIdentity.UserId`** is the `UserId` on a `UserMessage`, a form answer and an upload.
- **Deployment:** `agui.auth.*` and `agui.cors.allowedOrigins`, rendered as environment variables
  on the proxy's own deployment (`AgUi__Auth__*`, `AgUi__Cors__*`), not in the shared ConfigMap.
  The generic ingress renders with `agui.auth.enabled` or `agui.ingress.allowUnauthenticated`.

Decisions:

1. **The shared code went to `RockBot.UserProxy`, not a new project.** `UserAllowlist` needs
   nothing but `System.Security.Claims`, so it brings no ASP.NET Core into that library, and both
   proxies and both Dockerfiles already reference it (phase 4 put the attachment resolver there
   for the same reason). The authorization handler, which does need ASP.NET Core, is not shared:
   each proxy has its own twenty lines, and they differ (the proxy's also requires a verified
   address, and answers with a problem body, not a redirect).
2. **The proxy's settings are `AgUi:Auth:*`, not `Auth:*`.** The chart deploys both proxies, and
   Blazor's `Auth__*` keys are flat. A second proxy reading `Auth:*` would either collide with
   them or silently inherit Blazor's client id and allowlist. Under its own section, and set on
   its own deployment, neither proxy can see the other's.
3. **One issuer and a list of audiences, no provider registry.** Blazor's `AuthProviderRegistry`
   exists because each provider there is a handler, a button and a callback path. Validating a
   token needs none of that: an OIDC issuer is its address, and the discovery document gives the
   rest. Google is the default value of `Authority`, plus one special case, its second spelling
   of `iss`. Audiences are a list because every app has its own client id. A second issuer at the
   same time would be a second bearer scheme; nothing else would change.
4. **What is validated.** Signature (the issuer's JWKS, asymmetric algorithms only, so a token
   "signed" with the public key as an HMAC secret fails), `iss`, `aud`, `exp` (one minute of
   skew), then in authorization, on every request: `email_verified` is `true` and `email` is on
   the allowlist. `email_verified` is **required**, where the shared allowlist (and so Blazor)
   only refuses an explicit `false`: Blazor's provider is one the build chose, the proxy's is
   configuration, and an issuer that lets a user claim an address without verifying it would
   hand out allowlisted identities. `AgUi:Auth:RequireVerifiedEmail` turns that off.
5. **`UserId` is the email address.** The agent keys nothing on `UserId`: it logs it with the
   message and tags the turn's telemetry (`rockbot.user.id`), and multi-tenancy is a non-goal. So
   the id is for an operator reading who sent what, and the address is what the allowlist is
   written in. `AgUi:Auth:UserIdClaim` can name `sub` instead.
6. **Access tickets for browser-made requests.** `<img src>`, a new tab and `EventSource` can't
   send a header. A client with a token asks `POST /tickets` for a URL for one attachment or one
   thread's notify stream. The ticket is a payload and an HMAC-SHA256, good for 5 minutes and
   never past the token it was asked with, and it authenticates only a `GET` of what it names:
   the ticket handler refuses it anywhere else, so it can't be used as a token or traded for
   another ticket. Alternatives considered:
   - *The token in the URL.* A URL lands in history, logs and `Referer`.
   - *Signing the `url` in `rockbot.attachments`.* Events are replayed for 24 hours and history
     for ever, so the signature would have to live as long: a long-lived credential in a URL.
   - *A cookie.* It would make the API cookie-authenticated, and with it CSRF and
     `SameSite`/third-party-cookie rules, for a client on another origin.
7. **An attachment's ticket is in the path, and an HTML attachment's covers its directory.**
   Phase 4 serves attachments by path so that a deck's relative links resolve. A ticket in the
   query would be dropped from those links, so it is a path prefix
   (`/t/{ticket}/attachments/…`), and a ticket for an HTML file in a directory is accepted for
   files under that directory. A ticket for anything else, or for a file at the root, is for
   that file only. The cost: the sandboxed document can read its own URL, and so its ticket. It
   gets five minutes of read access to its own directory, which it has anyway.
8. **The ticket key is random per process.** No PVC and no Secret: a restart refuses tickets that
   had minutes to live, and the client asks again. Blazor needs a persisted key ring because its
   cookies last 14 days; nothing here lasts longer than a token. `AgUi:Auth:TicketKey` (Helm:
   `agui.auth.ticketKeyFromSecret`) gives several replicas one key.
9. **The notify stream ends when its token expires; a run finishes.** A stream that never ends
   must not outlive its credential, and ending it costs nothing: the client reconnects with a
   fresh token and `Last-Event-ID`. A run is bounded by `MaxTurnDuration`, and cutting it would
   cancel the turn (an aborted run cancels the session), so a run authorized at its start is
   allowed to end. Taking someone off the allowlist takes a restart either way: it is
   configuration.
10. **Errors.** `401` with `WWW-Authenticate: Bearer` (and RFC 6750's `error="invalid_token"`
    when a token was sent) for a missing or bad token or ticket; `403` for a valid identity that
    isn't allowed. Both are problem-details bodies like every other refusal. Neither names an
    address: the refused one goes to the proxy's log, where an operator needs it.
11. **The sign-in callback needed no change.** It stays anonymous. Starting a sign-in is in the
    authorized group, so only an allowed account can put a `state` on the tracker, and the
    callback relays nothing else (phase 4, decision 8).
12. **CORS.** No origins, no middleware and no headers. With origins: those origins, `GET`,
    `POST` and `DELETE`, any request header, `Location` and `WWW-Authenticate` exposed, never
    credentials. Request headers are not enumerated because the header allow-list protects an
    API that acts on something the browser attaches by itself; this one acts only on the
    `Authorization` a script sets. The middleware runs before authentication, so a preflight
    needs no token and a `401` carries the headers.
13. **Fail fast.** Enabled with no allowlist entry, no audience, a non-`https` authority, a bad
    ticket key or a wildcard origin, the proxy throws at startup. The chart fails on the first
    two at render time.
14. **The origin is checked, in both modes.** CORS stops a page reading a response, not sending
    a request, and a browser sends "simple" requests without a preflight: a `text/plain`
    `POST /agui`, a multipart `POST /attachments`, a body-less `POST /threads/{id}/cancel` or
    `/clear`. In network mode nothing else stood between a page on any site and those. Requiring
    a JSON content type would cover the first and not the others, so the check is on the one
    thing a page can't forge: a request to a client endpoint that carries `Origin` gets `403`
    unless the origin is a configured CORS origin or the proxy's own. "Its own" is
    `AgUi:PublicBaseUrl`'s origin, or any origin whose host and port equal the request's `Host`;
    the scheme is ignored there because a TLS-terminating ingress changes it. `Origin: null` is
    never allowed, which covers the sandboxed HTML attachments. No `Origin` passes: that is every
    non-browser client, navigations, and `<img>`. `/healthz` and the sign-in redirect are exempt
    (endpoint metadata on the group decides, so an endpoint added to the group is covered). The
    check runs after the CORS middleware, which still answers preflights, and before
    authentication.

Where the implementation differs from the design above:

1. **ID tokens only for Google.** The design says "ID or access tokens". A Google access token is
   opaque: validating one is a call to Google's tokeninfo or userinfo endpoint per request (or a
   cache of them), which says nothing about which app the token was issued to unless the proxy
   also checks `azp`. Any provider's *JWT* is accepted, access token or ID token, when its `aud`
   is a configured audience and it carries the email claims.
2. **`UserAllowlistOptions` is new.** The design moves "the `UserAllowlist` and options". Blazor's
   `AuthOptions` is mostly about its own sign-in (providers, cookie lifetime, forwarded headers),
   so only the two lists moved, as a base class.
3. **No data-protection key ring.** The design allows a PVC "if auth needs one". It doesn't.
4. **`POST /tickets` and `GET /t/{ticket}/attachments/{path}`** are not in the design's table.
5. **Network mode doesn't say "tailnet only".** The proxy can't tell what network it is on. The
   chart's guard is what stops a public ingress without auth.
6. **The origin check** is not in the design, which only says that with CORS "only the
   configured WASM origins may call the API". CORS headers alone don't make that true.

Open after phase 5:

- **Network mode and cross-site requests.** *Closed in phase 5* by the origin check (decision
  14). What it leaves, in network mode only:
  - *DNS rebinding.* A page whose hostname is re-pointed at the proxy is same-origin to the
    browser, and its `Origin` host equals `Host`. ASP.NET Core's `AllowedHosts` closes it; the
    proxy ships with `*` and the chart doesn't set it, because it can't know every name a
    tailnet gives the Service.
  - *Browsers that send no `Origin` on a cross-site form post* (older Firefox, and others of
    that age).
- **Revocation needs a restart.** The allowlist is read at startup.
- **One issuer.** Accepting Google and another provider at once needs a second bearer scheme.
- **Tickets and replicas.** Without a shared key a ticket only works on the replica that issued
  it, like the notify buffer and the question list.
- **No rate limit on failed tokens.** Each is a signature check; an ingress can limit them.

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

**Phase 3 tests** are in the same project: the interrupt mapping (the schema for every field kind,
the reason) and the registry's state machine as pure tests; both modes, the answer endpoint, expiry
and follow-up routing through the test host; and the conformance tests drive an interrupt and its
resume with `AGUIChatClient`, which implements both (`InterruptRequestContent`,
`InterruptResponseContent`).

**Phase 4 tests** are in the same project: every endpoint through the test host, with the fake
agent answering the request topics (the happy path, the agent not answering, validation, the
thread allow-list); attachments served from a temporary directory (containment, `nosniff`, the
sandbox policies), upload limits and types, and references on a run; the sign-in callback (a state
the proxy didn't issue, no state, no code, relayed once); the sign-in tracker and the attachment
reference mapping as pure tests; and one test over a real socket for an upload that doesn't
announce its length. The resolver's and presentation's own tests are in
`tests/RockBot.UserProxy.Tests`.

**Phase 5 tests** are in the same project, against a stand-in issuer: an RSA key made in the test
and handed to the bearer handler as its discovery document, so nothing reaches a network. Token
validation (audience, issuer, expiry, signature, no signature, the public key as an HMAC secret,
unverified and missing email, the allowlist by address and by domain); every mapped endpoint
refusing a request without a token, with a check that the list of endpoints is complete; the
user id reaching the agent; tickets (scope, tampering, expiry, each kind on the other's door);
the notify stream ending at expiry and a run finishing; CORS; the origin check (each shape of
un-preflighted cross-site request, every endpoint, `null`, same origin with and without TLS
termination, the public address, no origin); startup validation; and the
conformance test once with a bearer token. The allowlist's own tests are in
`tests/RockBot.UserProxy.Tests`.

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
2. **Interrupt as default.** *Decided: `interrupt`.* Should `interrupt` be the default question
   mode? It's what generic clients expect, but blocking new input is a real UX regression compared
   with Blazor. RockBot's own clients opt into `custom`; and since forms are opt-in, a client that
   never asks for them is not blocked at all (phase 3, decision 2).
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
     the saved-response archive (#418), which the `/saved` endpoints expose.

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
