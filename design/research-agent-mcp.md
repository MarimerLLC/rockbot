# ResearchAgent as an MCP Server

**Status:** Done (#717). MCP elicitation (#593), the `conversation` responder, the SDK 2.x
upgrade (#602, #607), hand-back (#608) and the Tasks-aware bridge (#658) came first; #717 built
the research MCP server (Phase 2), moved the council (Phase 3) and the primary agent (Phase 4)
onto it, and retired the A2A research path (Phase 5). "Current state" below describes the A2A
design this replaced.

## Problem

`ResearchAgent` was the original test scenario for A2A in rockbot. A2A was chosen largely
because it lets the callee turn around and ask the caller for more information
(`AgentTaskState.InputRequired`) — something MCP could not do cleanly at the time.

Two things have changed since:

1. **ResearchAgent never used that capability.** Nothing in `src/RockBot.ResearchAgent` returns
   `InputRequired`; its directives and system prompt explicitly say *"Do not ask clarifying
   questions"*. The multi-turn machinery exists only on the caller side (`RockBot.A2A`'s
   `InputRequiredHandler`) and is exercised by other agents, not by research.
2. **MCP now covers the case natively.** The 2026-07-28 spec and C# SDK 2.x add Multi
   Round-Trip Requests (MRTR) — a tool can stop mid-call and ask the client for input — and a
   redesigned Tasks extension for long-running tools with polling.

Meanwhile the research agent itself has drifted. Its source compiles against the current
framework via `ProjectReference`s, but it has had no substantive change since #409 (May 2026),
and the deployed image is pinned at `rockbot-research-agent:0.10.66` in
`deploy/k8s/research-agent-scaledjob.yaml` while the repo is at 0.15.x.

Semantically, research is a **tool** — question in, cited answer out — not a peer agent with
goals of its own. Modelling it as an MCP server lets every MCP-capable client use it (the
primary agent via the bridge, the council directly, external clients such as Claude Code)
without a bespoke A2A invocation path.

## Goals

- Expose research as a single MCP tool served by a standalone ASP.NET Core MCP server, on the
  same pattern as `McpServer.OpenRouter` (`AddMcpServer().WithHttpTransport()` + `MapMcp()`).
- Let the research server ask the caller a clarifying question when ambiguity would materially
  change the research — using MRTR elicitation, answered by #593's coordinator on the client
  side.
- Support multi-minute research runs without holding a tool call open past sensible limits.
- Keep the current research quality: same web tools, same `AgentLoopRunner` loop, same
  working-memory chunking, same High/Balanced tier and fallback chain.
- Retire the A2A research path (ScaledJob, queue, `ResearchAgentInvoker`, well-known-agent
  entry) once callers have moved.

## Non-goals

- Removing A2A from rockbot. `AdvisorCouncil`, `SampleAgent` and external agents still use it;
  this is a per-agent decision about research only.
- MCP **sampling** (research borrowing the caller's LLM). Research owns its model tiers and
  budget; #593 already leaves sampling out of scope, and this design keeps it that way.
- url-mode elicitation. Headless; declined, as in #593.
- Changing how research searches or synthesises. This is a transport and hosting change.

## Current state (before #717)

```
Primary agent ──invoke_agent──► agent.task.ResearchAgent (RabbitMQ)
                                      │
AdvisorCouncil ──ResearchAgentInvoker─┘   KEDA ScaledJob: one pod per message (max 3),
                                          scale to zero, EphemeralShutdownService exits
                                          after one task
                                      │
          agent.response.{caller} ◄───┘   Working status updates while running
```

- **Primary agent** calls asynchronously: `invoke_agent` returns immediately; the result is
  stored in working memory and a synthetic user turn announces it later
  (`A2ATaskResultHandler`). The conversation is never blocked on research.
- **AdvisorCouncil** calls synchronously: `ResearchAgentInvoker` owns a private reply queue
  and waits on a `TaskCompletionSource` per correlation id.
- Research runs are minutes long; `ModelBehavior.MaxToolIterationsOverride = 50`.

## What MCP v2 provides

| Feature | C# SDK 2.x surface | Relevance |
|---|---|---|
| Multi Round-Trip Requests | Tool throws `InputRequiredException` with `InputRequest.ForElicitation(...)` and an opaque `requestState`; client re-issues the same `tools/call` with `inputResponses`. `McpClient` resolves the loop through the registered `McpClientHandlers.ElicitationHandler`. | Replaces A2A `InputRequired`. Stateless — no session affinity. |
| Tasks extension | `ModelContextProtocol.Extensions.Tasks`; server `.WithTasks(IMcpTaskStore)`; client `CallToolAsTaskAsync` / `CallToolWithPollingAsync`. Inside a task the tool's `ElicitAsync` goes through the task store, so a running task can pause for input. A tool that throws `InputRequiredException` (MRTR) inside a task fails it in 2.2.0. | Long research runs without a single long-held request. |
| Stateless HTTP by default | `HttpServerTransportOptions.Stateless = true` | Any replica can serve any call. |
| Legacy server→client requests | `ElicitAsync` / `SampleAsync` / `RequestRootsAsync` deprecated (MCP9005) and throw in stateless mode | #593 is written against the 1.4 `elicitation/create` path; see Phase 1. |

rockbot is on `ModelContextProtocol` 1.4.0 today; 2.2.0 is current on NuGet. The experimental
Tasks from 1.3–1.4 are **not** wire- or API-compatible with the 2.x Tasks extension.

## Design

### The tool

One tool, `research`:

| Argument | Type | Purpose |
|---|---|---|
| `question` | string, required | What to research. |
| `context` | string, optional | Background the caller already has: who is asking, why, what is already known. |
| `constraints` | string, optional | Scope limits — time range, region, sources to prefer or avoid, depth. |

`context` and `constraints` are deliberately ordinary arguments. #593's `McpElicitationNote`
tells the calling agent which fields a declined elicitation wanted; if those map to real tool
arguments, the agent's retry carries them and the loop terminates. A clarifying question
whose answer has nowhere to go on retry is exactly the non-terminating case #593's design
warns about.

The result is the synthesised answer as text content, plus a structured block listing the
sources consulted.

### When research asks

The directive changes from "never ask" to "ask at most once, and only up front":

- The server runs a cheap **scoping pass** (Balanced tier, no tools) over `question`,
  `context` and `constraints` before spending any search budget. If the question is genuinely
  ambiguous in a way that would send the research in different directions ("Mercury — the
  planet, the element, or the band?"), it asks a single form-mode elicitation: a single enum
  of the candidate interpretations, and no free-text field. Running as a task, it must call
  `ElicitAsync`, which the Tasks extension routes through the task store; called as a plain
  `tools/call`, it throws `InputRequiredException` (MRTR). SDK 2.2.0 fails a task whose tool
  throws `InputRequiredException` ("MRTR and tasks cannot be composed via [McpServerTool] yet"),
  so the tool has to check which way it is running (see "How the tool tells tasks from plain
  calls" below). Note that `CallToolWithPollingAsync` (the council's planned client) opts into
  Tasks, so it runs research **as a task**, not as an MRTR call; only a plain `CallToolAsync` (an
  external client, or the bridge with `tasks` off) takes the MRTR path. The `conversation` responder never fills free text in from the user's conversation
  (it declines the whole form), so anything open-ended belongs in the tool's `context`
  argument instead.
- It never asks mid-research. A mid-run question means a declined answer wastes the search
  work already done; an up-front one costs one cheap LLM call.
- It never asks for anything that looks like a credential or a confirmation — #593's
  coordinator would decline those anyway (`McpSensitiveFieldDetector`, all-boolean rule).
- On `decline` or `cancel`, research proceeds with its best interpretation and says which one
  it chose at the top of the answer. It does not fail the call.

`requestState` carries the scoping result so the retried call skips straight to research. It
is an untrusted continuation token: protect it with ASP.NET Core Data Protection (encrypt +
MAC) with a short expiry (~10 minutes). A tampered or expired token is treated as absent —
the server just re-scopes.

### How the tool tells tasks from plain calls

The SDK does not tell a tool whether it is running inside a task. `ResearchCallMode.IsTask`
repeats the Tasks filter's own test (`McpTasksBuilderExtensions` in 2.2.0): the server's
`ExecutionModeSelector` allows a task (it returns `Optional` for `research`), the request is on
protocol `2026-07-28` or later, and the client opted into `io.modelcontextprotocol/tasks`, in its
capabilities or in the request's `_meta`. `CallToolAsTaskAsync` and `CallToolWithPollingAsync`
add that opt-in; `CallToolAsync` does not. `RockBot.ResearchAgent.Tests` calls the tool both ways
and fails if the check drifts from the SDK: a task that throws `InputRequiredException` fails on
the server, and a plain call that tries `ElicitAsync` on the stateless server never reaches the
client.

The tool also asks only a client that declares the elicitation capability; any other client
gets the most likely reading, stated at the top of the answer. A retried MRTR call never asks a
second time, so the round trip always ends.

### Depth

`research` takes an optional `depth`: `thorough` (the default) is the full run described above,
High tier and up to 50 iterations, and a broad question measured 7.5 minutes and 26 sources.
`quick` is a short lookup for callers on a tight budget: two or three sources and a per-run tool
budget (8 calls, 60 s) after which every tool tells the model to answer from what it has. The
budget is per run on purpose: the function-invoking client's iteration limit is per tier and
process-wide, so changing it per call would leak into concurrent runs. The council
asks for `quick` (`Council:ResearchDepth`); the primary agent leaves the default.

Both depths run on the High tier: depth bounds the work, not the model. (`quick` was Balanced
until a live council run showed the cost of that was judgement, not time.)

The 60 s box covers calls in flight, not just calls that start inside it: a call still running
when the box ends is cancelled and returns the "answer now" note. Checking only at the start let
one page whose server took 55.6 s to send its headers stretch a quick run to 98 s.

A quick answer is checked by the decision model (`DecisionModel__*`, purpose `ResearchAnswer`)
instead of the loop's LLM completion evaluator. The evaluator is built for the primary agent's
turns: on a research answer it fired on a keyword trigger meant for claimed changes, and cost a
Low-tier call before its re-prompt. The decision model asks two questions in one call: does the
answer miss the question, and does it run far past "a few short paragraphs". On a yes, the answer
is rewritten once from the draft, with no tools. Without a configured decision model the LLM
evaluator stays on; a failed decision call keeps the answer as it is.

### The result

Text content: the answer, prefixed with `Researched as: …` (a reading was chosen) or
`Interpreted as: …` (the question was ambiguous and nobody chose), then a second text block,
`Sources consulted:`, listing the pages `web_browse` read. `structuredContent` carries the same
as `{answer, interpretation?, interpretationChosen?, sources: [{url, title?}]}`.

The question's choices are the scoping pass's interpretations (`TitledSingleSelectEnumSchema`).
The bridge's hand-back note and its responders' prompts show each option as `id ("label")`.

### Who answers the question

Two responders ship, chosen per server (`responder` in the server's elicitation policy):

- `llm` (the default) is deliberately narrow: no conversation, no memory, no tools; it answers
  only from the in-flight call's own arguments. If the caller put the answer in `context`, it
  can transcribe it; if not, it declines, the note goes back to the agent, and the agent retries
  with `context` filled in or asks the user.
- `conversation` answers from the calling conversation's recent turns (secret-shaped text
  redacted), and only choice fields — never free text or numbers, never memory or tools. "Which Mercury did you mean?" offered
  as a choice is usually settled by what the user already said, so this is the right choice for
  the research server: `"responder": "conversation"`, `"responderTimeoutMs": 30000`, in that
  server's own elicitation block. The research server must therefore ask its clarifying
  question as an enum of candidate interpretations — which the design above already does — and
  put any free-text follow-up in the tool's `context` argument instead of eliciting it.

Neither can ask the user mid-call; a question the conversation does not settle is declined and
handed back through the note. Handing it to the calling agent's own loop — so it can ask the
user and resume — is tracked in #602 and needs MCP C# SDK 2.x.

### Long-running calls

The MCP bridge today is synchronous per call: `DefaultTimeoutMs` 60 s, per-server
`ToolTimeoutMs`, capped by `MaxTimeoutMs` 900 s. Research could fit under the cap with a
per-server `toolTimeoutMs` of ~10 minutes, but that changes the primary agent's experience:
today `invoke_agent` returns immediately and the answer arrives later as a synthetic turn;
through a blocking MCP tool call the agent's loop — and the user — would wait.

So the two callers want different things:

- **AdvisorCouncil** already waits synchronously. It can call the research server directly
  with `McpClient.CallToolWithPollingAsync` and drop `ResearchAgentInvoker` and its private
  reply queue.
- **Primary agent** should keep asynchronous delivery. That needs the bridge to understand
  Tasks: start the call with `CallToolAsTaskAsync`, return a "research started" tool result
  immediately, poll in the background, and on completion reuse the A2A pattern — store the
  result in working memory and inject a synthetic turn naming the key. This is new bridge
  work, and is the largest piece of this design.

The Tasks-aware bridge exists (#658; see `design/mcp-bridge.md`, "Long-running tools"). Until the
research server does, the primary agent stays on A2A research.

### Hosting

- `RockBot.ResearchAgent` becomes an ASP.NET Core app: `AddMcpServer().WithHttpTransport()`
  (stateless) `.WithTasks(store)`, `MapMcp()`, `MapHealthChecks("/health")`, ClusterIP service
  only.
- A plain `Deployment`, not a ScaledJob. Idle cost is low — the heavy work is remote LLM and
  search calls. Start at one replica with `InMemoryMcpTaskStore`; a pod restart loses
  in-flight tasks, which the caller sees as a failed poll and can retry. Moving to more
  replicas requires a durable, shared `IMcpTaskStore` and is deferred until load justifies it.
- `EphemeralShutdownCoordinator` / `EphemeralShutdownService` go away. The null stores
  (`NullConversationMemory`, `NullSkillStore`, `NullFeedbackStore`) stay — the pod is still
  stateless apart from per-task working memory.
- Working memory stays in-process, namespaced `research/{taskId}`, now shared across
  concurrent tasks in a long-lived process. Its TTL bounds growth; confirm it is sized for
  several concurrent runs.
- Concurrency: cap concurrent research tasks per pod (a `SemaphoreSlim`, default 3 — matching
  today's `maxReplicaCount`) so a burst cannot exhaust LLM quota.

As built (#717):

- `deploy/k8s/research-agent.yaml`: Deployment `rockbot-research-agent` (one replica,
  `Recreate`), Service `research-agent` on port 8080; the endpoint is
  `http://research-agent:8080/mcp`, health at `/health`.
- `Research:MaxConcurrentRuns` (default 6, raised from 3 after a live council run: pre-research
  plus five persona calls at once left half of them queued past their timeout) is enforced by
  `GatedResearchRunner` around the research loop. Scoping and asking happen outside
  the gate, so a queued run never holds a question open.
- Each run (`ResearchRunner`) works in `research/{runId}` and clears that namespace when it
  finishes. Page chunks get a 60-minute TTL (`WebTools.ChunkTtlMinutes`, raised from 20) so they
  outlive a long run; clearing at the end bounds growth in the long-lived process.
- `requestState` is sealed by `ResearchScopeToken`: Data Protection (time-limited protector,
  `Research:ScopeTokenLifetime`, default 10 minutes), bound to a SHA-256 of the call's arguments.
  The key ring is in memory, so a restart invalidates outstanding questions; the retried call
  then re-scopes and proceeds with the most likely reading, without asking again.
- The task store is `InMemoryMcpTaskStore` with a 2-hour time-to-live.
- The A2A receiver ran alongside the MCP endpoint until the council moved, then went with
  Phase 5. The server has no message broker now; the agent host's bus is in-process.

## Phases

### Phase 0 — MCP elicitation (done)

#593 landed MCP elicitation on the 1.4 SDK: per-server policy and responders, the agent-facing
note (which says whether a declined field is a tool parameter), and the calling `SessionId` on
every in-flight call. The `conversation` responder that answers research clarifications from
the calling conversation followed on top of it.

### Phase 1 — Upgrade to MCP C# SDK 2.x

Tracked in #602. Repo-wide: `RockBot.Agent`, `RockBot.Tools.Mcp`, and the `McpServer.*` projects.
#602 also makes it a requirement that a server's question answerable only from the caller's
context is handed back to the calling agent's own loop (and, when needed, to the user) rather
than answered in place — which is what research's clarifying question needs.

- Resolve MCP9004 / MCP9005 / MCP9006 diagnostics.
- The in-repo MCP servers become stateless by default. Check each for session reliance; set
  `Stateless = false` only where genuinely needed.
- **Verify #593 under MRTR.** Per the SDK docs, the client-side `ElicitationHandler` also
  services MRTR input requests, so `McpElicitationCoordinator` should carry over unchanged.
  Two things to test rather than assume: (a) `McpElicitationCallScope` attribution — with
  MRTR the elicitation arrives as part of the tool call's own result cycle, which may make
  attribution exact where #593 documents it as approximate; (b) `MaxPerCall` still bounds
  rounds when the SDK is driving the retry loop.
- Add an MRTR fixture to `McpServer.BinaryFixture` (or a sibling) so the coordinator is
  covered end-to-end.

### Phase 2 — Research MCP server

- Convert `RockBot.ResearchAgent` to an MCP server hosting the `research` tool; keep the
  A2A receiver alongside it temporarily so both paths work during migration.
- Scoping pass + MRTR elicitation; protected `requestState`.
- Tasks extension with `InMemoryMcpTaskStore`; concurrency cap.
- New `Deployment` + `Service`; image built from the current tree (fixes the 0.10.66 pin).
- Directive and system-prompt update: ask at most once, up front.

### Phase 3 — Council switches to MCP (done, #717)

As built: `ResearchMcpClient` (`src/RockBot.AdvisorCouncil/Tools`) replaces
`ResearchAgentInvoker`. It calls `research` with `CallToolAsTaskAsync` and polls the task
itself rather than through `CallToolWithPollingAsync`, which keeps the task id to itself: on a
timeout (`Council:ResearchAgentTimeoutSeconds`) the client cancels the task on the server, so a
persona that gave up doesn't leave a run spending budget. Research's question arrives as the
task's input request and is answered by #593's coordinator with the `llm` responder, from the
call's own arguments. Pre-research and persona calls pass the council question and the asking
lens as `context`, so those usually settle it. `Council:ResearchServerUrl` defaults to
`http://research-agent.rockbot.svc.cluster.local:8080/mcp`.

Planned:

- Replace `ResearchAgentInvoker` with an `McpClient` + `CallToolWithPollingAsync` wrapper,
  registering #593's coordinator as its elicitation handler.
- Update `design/advisor-council.md`.

### Phase 4 — Tasks-aware bridge; primary agent switches

- Bridge (done, #658): per-server opt-in (`"tasks": true` in `mcp.json`) to invoke via
  `CallToolAsTaskAsync`, return immediately, poll in the background, and deliver the result
  as a working-memory entry + synthetic turn. A task's questions go through the server's
  elicitation policy, including hand-back to the conversation; tasks survive a restart of the
  agent through a task ledger; caps, `mcp_cancel_task` and cancel-on-removal bound them. See
  `design/mcp-bridge.md`, "Long-running tools".
- Primary agent (done, #717): research is an MCP tool. The `ResearchAgent` entry is gone from
  `well-known-agents.json`, the primary directive points factual lookups at the `research`
  tool, and the research server no longer announces its A2A card
  (`Research:AnnounceOverA2A`, default false), so the primary agent can't rediscover the A2A
  path. The `mcp.json` entry on the agent's PVC:

  ```json
  "research": {
    "type": "http",
    "url": "http://research-agent.rockbot.svc.cluster.local:8080/mcp",
    "transportMode": "streamable-http",
    "tasks": true,
    "taskTimeoutMinutes": 30,
    "elicitation": { "mode": "handback" }
  }
  ```

  The PVC's `well-known-agents.json` and `directives.md` are seeded no-clobber, so a running
  deployment needs them edited by hand, and the persisted directory (`known-agents.json`)
  forgets the A2A entry once it stops being announced (24-hour TTL, pruned at startup).

### Phase 5 — Retire the A2A research path (done, #717)

As built: the research server no longer references `RockBot.A2A` or RabbitMQ;
`ResearchAgentTaskHandler`, `research-agent-scaledjob.yaml`, `research-agent-queue-init.yaml`
and `Research:AnnounceOverA2A` are gone. The KEDA secret script was shared with the council's
ScaledJob, so it stays, renamed `keda-rabbitmq-secret.sh`. The cluster-side cleanup (the
ScaledJob, deleted by name because the old manifest also defined the council's
TriggerAuthentication, and the `rockbot.ResearchAgent.*` queues) is in the header of
`deploy/k8s/research-agent.yaml`.

Planned:

Remove the A2A receiver from the research server, `research-agent-scaledjob.yaml`,
`research-agent-queue-init.yaml`, `research-agent-keda-secret.sh`, the queue, and the
ResearchAgent sections of `docs/a2a.md`.

## Open questions

1. **Tasks-aware bridge scope.** Resolved: #658 built it as the bridge's general
   "long-running MCP tool" capability, opted into per server, not as something research-only.
2. **Responder quality.** Measure how often the `conversation` responder settles research
   clarifications versus declining them back to the agent, before investing in the #602
   hand-back path for this server.
3. **Trust gating.** A2A `InputRequired` answers are gated by `IAgentTrustStore`. Elicitation
   uses per-server policy in `mcp.json` instead (choosing the `conversation` responder is the
   opt-in). Is per-server policy sufficient for research, or should the hand-back path in #602
   consult the trust store too?
4. **Direct external access.** Exposing the research server to clients outside the cluster
   (e.g. Claude Code) needs authentication on the MCP endpoint. Out of scope here; ClusterIP
   only until it is designed.
5. **Durable task store.** When — if ever — research needs more than one replica, which store
   backs `IMcpTaskStore`?

## References

- PR #593 and `design/mcp-elicitation.md` — client-side elicitation coordinator.
- `design/mcp-bridge.md` — bridge message flow and timeouts.
- `docs/a2a.md` — current invocation, `InputRequired`, ephemeral agent pattern.
- `design/advisor-council.md` — council's use of research.
- [Announcing v2.0 of the official MCP C# SDK](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/)
- [csharp-sdk v2.0.0 release notes](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.0.0)
- [MCP C# SDK 2.0: Stateless HTTP, Interactive Tools and a Practical Migration Path](https://benjamin-abt.com/blog/2026/08/03/mcp-csharp-sdk-2/)
