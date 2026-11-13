# AG-UI User Proxy

**Status:** Proposed (2026-11-12). Not started.

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
   /threads/{id}/messages`, network-mode auth, Helm chart, Dockerfile.
2. **Notify stream.** Routing frontend, ring buffer, `Last-Event-ID`, resync.
3. **MCP questions.** Both modes, with `responseSchema` built from `McpQuestionField`.
4. **REST extras.** Attachments, feedback, saved responses, MCP sign-in, clear context, agent info.
5. **App auth.** Bearer tokens, the shared allowlist, CORS.
6. **A reference client.** Probably a small Blazor WASM app on `AGUIChatClient` and the notify
   stream; it can live in this repo as a sample.

## Testing

**Already exercised by the CLI.** `rockbot chat --output jsonl` (#797, [`docs/cli.md`](../docs/cli.md))
writes one turn's bus traffic as AG-UI events built with `AGUI.Abstractions` 1.0.1: the run lifecycle,
progress as activity, tool calls, task lists as state, subagent lifecycle, the reply, and attachments
and origins as `CUSTOM rockbot.*` events. The mapping lives in `TurnEventMapper`
(`src/RockBot.UserProxy.Cli`). Phase 1 can move it into a shared library and reuse it, rather than
writing a second one.

- **Unit.** The `AgentReply` → event mapping and question-mode state are pure functions over
  recorded envelopes. Use `RockBot.Messaging.InProcess` for the proxy service.
- **Conformance.** Drive `POST /agui` with `AGUI.Client`'s `AGUIChatClient` in an integration test,
  and check that its parser accepts every stream the proxy emits, interrupts and resume included.
- **Live.** Run against the cluster agent, as with the CLI (`reference_rockbot_cli`), plus one
  third-party AG-UI client to check interoperability.

## Open questions

1. **The notify stream's shape.** Is reusing AG-UI event JSON outside a run the right call, or does
   it confuse clients that validate streams strictly? The alternative is plain RockBot JSON
   (`AgentReply` and friends), which is simpler and honest about not being AG-UI.
2. **Interrupt as default.** Should `interrupt` be the default question mode? It's what generic
   clients expect, but blocking new input is a real UX regression compared with Blazor.
3. **What history replays.** Does conversation history include unsolicited replies (subagent
   results, scheduled tasks) with enough metadata to rebuild `ReplyOrigin` on reload? If not,
   `rockbot.resync` loses anchors.
4. **One thread or many.** Should the proxy allow more than one thread id (say, one per device)?
   That's cheap for the proxy, but every extra session splits history and working memory on the
   agent side.
5. **Protobuf.** It's skipped for now (SSE is mandatory; protobuf is optional and covers a subset of
   events in .NET). Revisit only if a mobile client needs it.
