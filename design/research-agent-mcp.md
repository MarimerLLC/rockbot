# ResearchAgent as an MCP Server

**Status:** Proposed. MCP elicitation (#593) and the `conversation` responder are in place;
the remaining phases start with the MCP C# SDK 2.x upgrade (#602).

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

## Current state

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
| Tasks extension | `ModelContextProtocol.Extensions.Tasks`; server `.WithTasks(IMcpTaskStore)`; client `CallToolAsTaskAsync` / `CallToolWithPollingAsync`. MRTR flows through the task store, so a running task can pause for input. | Long research runs without a single long-held request. |
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
  planet, the element, or the band?"), it throws `InputRequiredException` with a single
  form-mode elicitation: a single enum of the candidate interpretations, and no free-text
  field. The `conversation` responder never fills free text in from the user's conversation
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

Until the Tasks-aware bridge exists, the primary agent stays on A2A research.

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

### Phase 3 — Council switches to MCP

- Replace `ResearchAgentInvoker` with an `McpClient` + `CallToolWithPollingAsync` wrapper,
  registering #593's coordinator as its elicitation handler.
- Update `design/advisor-council.md`.

### Phase 4 — Tasks-aware bridge; primary agent switches

- Bridge: per-server opt-in (`"tasks": true` in `mcp.json`) to invoke via
  `CallToolAsTaskAsync`, return immediately, poll in the background, and deliver the result
  as a working-memory entry + synthetic turn.
- Primary agent: research is now an MCP tool; remove the `ResearchAgent` entry from
  `well-known-agents.json`.

### Phase 5 — Retire the A2A research path

Remove the A2A receiver from the research server, `research-agent-scaledjob.yaml`,
`research-agent-queue-init.yaml`, `research-agent-keda-secret.sh`, the queue, and the
ResearchAgent sections of `docs/a2a.md`.

## Open questions

1. **Tasks-aware bridge scope.** Is Phase 4 worth building for research alone, or is it the
   general "long-running MCP tool" capability the bridge needs anyway? If other servers will
   want it, design it generally; if not, the primary agent could stay on A2A research
   indefinitely and Phase 5 shrinks to the council's path.
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
