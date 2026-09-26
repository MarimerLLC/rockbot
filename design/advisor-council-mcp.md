# AdvisorCouncil as an MCP Server

**Status:** Proposed. Follows the ResearchAgent move (`design/research-agent-mcp.md`), and uses
the MCP elicitation stack from #593 and the `conversation` responder from #605. The hosting and
long-running-call pieces depend on the MCP C# SDK 2.x upgrade (#602).

## Problem

`AdvisorCouncil` takes one input today: the question text in an A2A `AgentTaskRequest`. A
council's value depends on more than the question — what the user is trying to achieve, what
constrains them, what options are on the table, how much risk they can bear, how far ahead they
are looking. When that is missing, the personas guess, and the synthesis is generic.

Two ways to get it:

1. **The caller supplies it.** The calling agent has the conversation; it can pass the context
   along with the question.
2. **The council asks.** When something that would change the deliberation is missing, the
   council asks a clarifying question before it starts.

A2A would allow (2) through `InputRequired`, but the council never uses it. MCP now allows it
through elicitation (#593), with a per-server policy for who answers and what may be sent. This
document moves the council to an MCP tool and designs both paths, with (1) doing most of the
work and (2) as a narrow, safe fallback.

Also worth fixing on the way:

- `design/advisor-council.md` documents optional request headers (`rb-council-personas`,
  `rb-council-critique`, `rb-council-pre-research`); the handler does not read them — it takes
  only the question text. Tool arguments are the natural home for these overrides.
- `deploy/k8s/advisor-council-scaledjob.yaml` pins `rockbot-advisor-council:0.10.66`, the same
  stale image as ResearchAgent's ScaledJob.

## Goals

- Expose the council as one MCP tool, `advise`, with structured arguments that carry the
  context a council needs.
- Let the council ask **at most one** clarifying question, **up front**, and only in forms the
  bridge's elicitation policy can answer safely.
- Keep web content out of the question path: nothing the council reads from research may cause
  it to ask anything.
- Keep the council working as an MCP *client* of ResearchAgent, including research's own
  clarifying questions.
- Retire the A2A council path once callers have moved.

## Non-goals

- Changing personas, selection, critique or synthesis beyond the inputs they receive.
- Free-text answers drawn from the user's conversation (see "Answering the question").
- Questions from individual personas mid-deliberation. One question, from the council, before
  it starts.

## Current state

```
Primary agent ──invoke_agent(AdvisorCouncil, advise)──► agent.task.AdvisorCouncil (RabbitMQ)
                                                              │  KEDA ScaledJob, one pod per task
                                                              ▼
        CouncilOrchestrator.RunAsync(question, taskId)
          1. SelectStep        personas, pre_research?, critique?     (Balanced, no tools)
          2. PreResearchStep   one research call on the question      (conditional)
          3. PersonaStep ×N    parallel; research-enabled personas get
                               ResearchAgentInvoker as a tool         (Balanced)
          4. CritiqueStep      revise views, name tensions            (conditional)
          5. SynthesizeStep    structured JSON + prose                (High)
```

A run takes roughly 30 s to 3 min. Research reaches the council from two places — PreResearch
and research-enabled personas — and research reads untrusted web pages.

## The tool

One tool, `advise`:

| Argument | Type | Purpose |
|---|---|---|
| `question` | string, required | The decision or idea to deliberate on. |
| `context` | string | Free text from the caller: goals, success criteria, constraints, stakeholders, what has been tried, what the user already believes. |
| `options` | string[] | The options being weighed, when there are named options. |
| `time_horizon` | enum `weeks` · `months` · `years` · `decade_plus` | How far ahead the decision matters. |
| `risk_appetite` | enum `low` · `moderate` · `high` | How much downside the user can accept. |
| `reversibility` | enum `easily_reversed` · `costly_to_reverse` · `irreversible` | Replaces an "is this reversible?" boolean (see below). |
| `personas` | string[] | Force the persona set (replaces the unimplemented `rb-council-personas`). |
| `critique` / `pre_research` | enum `auto` · `on` · `off` | Replace the unimplemented `rb-council-critique` / `rb-council-pre-research`. |

Everything but `question` is optional. The tool description tells the calling agent to fill
`context` and the enums from what the user has said; that is where the council's free text comes
from — written by the agent, which already decides what every tool call sends.

The result keeps the existing output schema (synthesis prose as text content, the full object
as structured content), plus one addition: `assumptions` — what the council assumed for anything
it asked about and did not get, or never asked about. The synthesis is only as good as those
assumptions, so the caller should see them.

## Clarifying questions

### When

Only in the **Select** step, before PreResearch, before any persona runs, before any research
result exists. Select already decides the plan (personas, pre-research, critique); it gains one
more output — what, if anything, is missing that would change that plan — and the council asks
about it at most once.

This is a structural rule, not a prompt instruction. Elicitation is only available to the code
path that runs Select; PreResearch, personas, critique and synthesis have no way to raise one.
Web content arrives through research, research happens after Select, so injected page content
can never become a question the council puts to the user's side. The same rule keeps asking
cheap: a declined question costs one short Balanced call, not a deliberation.

### What, and in what form

The bridge answers the council through the `conversation` responder (#605): recent conversation
only, **choice and number fields only**, never free text, never booleans or yes/no choices
(#593 treats those as decisions a person must make). The council's questions are shaped to fit:

| What's missing | Ask as | Answered by |
|---|---|---|
| Which of the options named in the question the user leans toward | single- or multi-select of those options | `conversation`, from what the user said |
| Time horizon, risk appetite | the enums above | `conversation` |
| Reversibility, and other factual yes/no questions | **named options** (`easily_reversed` / `costly_to_reverse` / `irreversible`), never a boolean or yes/no enum | `conversation` |
| Kind of decision (strategic / technical / personal / organizational) — drives persona choice | single-select | `conversation` |
| Goals, constraints, stakeholders, priorities | **not elicited** — early return naming `context` (below) | the calling agent, on retry |

Rules for the form:

- **One elicitation, choice and number fields only.** Ask only what would change the plan;
  everything else is an assumption, stated in `assumptions`.
- **Factual yes/no as named options.** A boolean or `["yes","no"]` is declined by #593 as a
  confirmation. `["easily_reversed","irreversible"]` is a fact the conversation can settle.
- **Never elicit free text.** The `conversation` responder declines a request containing *any*
  free-text field — whole, including its choice fields — so mixing `context` into the form
  would lose the answers the council could have had. When free text is what is missing, the
  council does not ask for it; it returns early (below) and the caller supplies `context` as an
  argument, which is where the caller's free text belongs anyway.

### When the answer does not come — or cannot be asked for

A decline or cancel is normal, not an error. For the choice fields, the council proceeds: it
picks the most defensible assumption, records it in `assumptions`, and lets it lower
`confidence` where it matters.

Missing free text is handled without asking. If the question is answerable without `context`,
the council proceeds and says what it assumed. If it is not — the question is too thin to
deliberate on at all — Select returns early with a short result that says so and names what
`context` (and which enums) should cover, instead of spending a full council run on guesses. The
agent fills them from the conversation, or asks the user, and calls `advise` again. Nothing
expensive has run, so the retry is cheap.

### Answering what only the user knows

Risk appetite and priorities often are not in the conversation. Today that costs one round
trip: the council's question is declined (or the council returns early for `context`), the
agent asks the user, then calls `advise` again. Acceptable, because nothing expensive has run
yet. #602's hand-back — the question goes into the calling agent's own loop, which can ask the
user and resume the same call — removes the round trip; the council is the strongest case for
it.

## The council as an MCP client of ResearchAgent

The council calls ResearchAgent (PreResearch, and research-enabled personas). Once research is
an MCP server that may ask its own up-front clarifying question, the council's MCP client
receives that question.

- The `conversation` responder cannot answer it: the council's call carries no user session, so
  the responder declines by design.
- The council registers #593's `McpElicitationCoordinator` on its research client with the
  **`llm` responder**, which answers only from the call's own arguments — nothing reaches
  research that research was not already sent.
- The council passes a rich `context` into every research call (the council question, the
  caller's `context`, and the persona's angle), so those arguments usually settle research's
  question. A decline is fine: research proceeds with its best interpretation and says which.

This replaces `ResearchAgentInvoker` (A2A, private reply queue) with an `McpClient` call —
the council waits synchronously today, so `CallToolWithPollingAsync` fits.

## Hosting and delivery

Same shape as ResearchAgent:

- ASP.NET Core MCP server, stateless HTTP, one `advise` tool, `MapMcp()` + health check,
  ClusterIP only. A plain `Deployment` replaces the KEDA ScaledJob and the ephemeral-shutdown
  pieces; the image is built from the current tree, which also fixes the `0.10.66` pin.
- Runs of 30 s–3 min need the MCP **Tasks** extension (#602) for the call itself, and a
  Tasks-aware bridge so the primary agent keeps asynchronous delivery (result later, as a
  working-memory entry and synthetic turn, as A2A results arrive today). That bridge work is
  shared with ResearchAgent — build it once.
- Concurrency cap per pod (a council fans out to several model calls); start at one replica
  with an in-memory task store, as for research.

Operator configuration on the primary agent's bridge:

```json
"advisor-council": {
  "type": "streamable-http",
  "url": "http://advisor-council:8080/mcp",
  "toolTimeoutMs": 300000,
  "elicitation": { "responder": "conversation", "responderTimeoutMs": 30000, "maxPerCall": 1 }
}
```

`maxPerCall: 1` makes "at most one question" a bridge-enforced limit, not just a council
convention.

## Phases

1. **ResearchAgent on MCP** (`design/research-agent-mcp.md`), including the Tasks-aware bridge.
   The council keeps calling research over A2A until research's MCP server exists.
2. **`advise` arguments on the existing A2A council.** Accept `context`, `options` and the enums
   as an A2A `data` part and thread them through Select, personas and synthesis; add
   `assumptions` to the output. This is useful on its own and de-risks the MCP move.
3. **Council research client on MCP.** Replace `ResearchAgentInvoker` with `McpClient` +
   coordinator + `llm` responder.
4. **Council as an MCP server**, with Select-only elicitation and the early "too thin" return.
5. **Primary agent switches** to the `advise` MCP tool; remove `AdvisorCouncil` from
   `well-known-agents.json`.
6. **Retire the A2A council path** — ScaledJob, queue, queue-init job, KEDA secret, and the
   A2A sections of `design/advisor-council.md`.

## Open questions

1. **Ask at all?** A council gives advice, not actions; stating assumptions clearly may serve the
   user better than a question they have to answer. Worth measuring how often Select's
   "missing" output changes the plan versus only the wording, before enabling elicitation.
2. **Factual booleans.** Named options work, but a server-side marker for "this boolean is a
   fact, not a confirmation" would let the coordinator answer it. Only if a real server needs
   one.
3. **Where `assumptions` surface.** In the synthesis prose, in structured content, or both — and
   whether the calling agent should be told to confirm the load-bearing ones with the user.
4. **Persona-level gaps.** A persona may discover mid-run that it needs something the user never
   said. Today that becomes an assumption. Worth revisiting only after #602, when a question can
   reach the user without restarting the council.

## References

- `design/advisor-council.md` — current council design.
- `design/research-agent-mcp.md` — ResearchAgent move; shared hosting and bridge work.
- `design/mcp-elicitation.md` — elicitation policy, the `llm` and `conversation` responders,
  decision fields, the agent-facing note.
- #593 (elicitation), #605 (`conversation` responder), #602 (MCP C# SDK 2.x, Tasks, hand-back to
  the caller's loop), #603 (operator policy and register/unregister).
