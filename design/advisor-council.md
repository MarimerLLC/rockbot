# Advisor Council Agent

## Inspiration

The multi-persona deliberation pattern isn't new — it appears across the prompting community as "council of experts," "panel of advisors," and similar variants. The immediate prompt for building it into rockbot was JD Meier's "Council of Giants" post: https://www.linkedin.com/posts/jdmeier_council-of-giants-activity-7458556131225784321-JIkl

What this design adds is making the council a first-class service in the rockbot swarm — callable as a tool (an MCP server since #758; an A2A agent before), persona library as data, selector-driven gating, integrated with the research server for fact-finding — rather than a one-shot prompt template.

## Problem

The agent (and its users) regularly face open-ended questions where a single LLM pass — even a long one with tools — produces shallow or one-sided answers. "Should we adopt X?" "Is this design sound?" "What are we missing?" These benefit from being examined from multiple framings, not from running one bigger loop. Today rockbot has no structured way to do that: questions either get a single-perspective answer from the primary agent, or get punted to research (which surfaces facts, not judgment).

The deeper issue is that "wisdom" — the kind of thinking that surfaces tensions, names what's at stake, and integrates competing considerations — emerges from explicit perspective-taking, not from telling one model to "be wise." Multi-perspective reasoning needs to be a workflow, not a prompt.

## Goals

- Take a question or idea and return integrated, multi-perspective guidance with explicit tensions and confidence.
- Make the perspective set transparent and tunable (personas as data on the PVC, hot-reloaded).
- Let personas reach for facts when they need them, by invoking the research server. (Originally over A2A; since #717 it is the research server's MCP `research` tool, through `ResearchMcpClient` — see `design/research-agent-mcp.md`.)
- Surface the council as a tool the primary agent can call before its own consequential decisions: the MCP tool `advise` (#758; an A2A skill before, see `design/advisor-council-mcp.md`).
- Keep cost and latency bounded with selector-driven gating (which personas, with/without research, with/without critique).

## Non-goals

- Replacing the primary agent's decision-making. The council returns guidance; the caller decides what to do with it.
- Persona personality theatre. Personas are framings, not characters — short, focused system prompts, no role-play.
- Real-time interaction. Council runs are batch (typically 30s–3min) and return a single structured result.
- Replacing `ResearchAgent`. The council reuses it for fact-finding; it does not duplicate web search.

## Architecture overview

> **As built (#758):** the entry point is the MCP tool `advise` (`CouncilTools`) on a
> long-lived Deployment, not an A2A handler on an ephemeral pod, and orchestration is the
> imperative `CouncilOrchestrator` rather than a MAF graph. The stages are as drawn. See
> "MCP surface" below and `design/advisor-council-mcp.md`.

```
                   ┌──────────────────────────────────────────────┐
 MCP tools/call     │                 CouncilTools                 │
   advise           │   (as a task: result delivered by polling)   │
                   └────────────────────┬─────────────────────────┘
                                        │
                                        ▼
                   ┌──────────────────────────────────────────────┐
                   │                CouncilWorkflow                │
                   │                (MAF graph)                    │
                   │                                                │
                   │  ┌────────┐    ┌──────────────┐               │
                   │  │ Select │───▶│ PreResearch? │──┐            │
                   │  └────────┘    └──────────────┘  │            │
                   │       │                          │            │
                   │       ▼                          ▼            │
                   │  ┌────────────────────────────────────┐       │
                   │  │   Fan-out (parallel personas)      │       │
                   │  │   ┌──────────┐  ┌──────────┐  ...  │       │
                   │  │   │ persona  │  │ persona  │       │       │
                   │  │   │  (call   │  │ (Loop +  │       │       │
                   │  │   │  only)   │  │ Research)│       │       │
                   │  │   └──────────┘  └──────────┘       │       │
                   │  └─────────────────┬──────────────────┘       │
                   │                    │                          │
                   │                    ▼                          │
                   │           ┌───────────────┐                   │
                   │           │  Critique?    │                   │
                   │           └───────┬───────┘                   │
                   │                    │                          │
                   │                    ▼                          │
                   │           ┌───────────────┐                   │
                   │           │  Synthesize   │ (High tier)       │
                   │           └───────┬───────┘                   │
                   └────────────────────┼──────────────────────────┘
                                        │
                                        ▼
                              CallToolResult
                  (text content + structuredContent)

                   ┌────────────────────────────┐
                   │   /data/advisor-council/   │
                   │   personas/*.md            │ ◄── hot-reloaded
                   └────────────────────────────┘
```

`CouncilWorkflow` is a Microsoft Agent Framework (MAF) workflow. Selector output drives the conditional edges (pre-research, critique) and the per-persona branch shape (call-only vs. agentic loop with `ResearchAgent` tool). `AgentLoopRunner` wraps each persona branch that has tools, preserving cross-cutting concerns (iteration budget, completion eval, metrics).

## Personas (initial fixed set)

Six perspectives, chosen to cover the dimensions on which open-ended decisions most often go wrong:

| id | role | default research |
|---|---|---|
| `skeptic` | Challenges assumptions, surfaces counterexamples, names failure modes | false |
| `ethicist` | Examines harms, fairness, dignity, second-order moral effects | false |
| `engineer` | Feasibility, complexity, edge cases, technical risk | true |
| `economist` | Incentives, costs, opportunity cost, market dynamics | true |
| `long_term` | 5–10 year horizon, path dependencies, irreversibility | false |
| `user_advocate` | Lived experience of affected people, accessibility, dignity-in-use | false |

Each persona is a markdown file with YAML frontmatter:

```markdown
---
id: skeptic
name: The Skeptic
description: Challenges assumptions and surfaces failure modes
default_research: false
---

You are the Skeptic on an advisor council...
[system prompt body]
```

Files live at `/data/advisor-council/personas/{id}.md` on the PVC, hot-reloaded via the same `IFileWatcher` pattern as `directives.md`. The image ships defaults; the running pod can be updated with `kubectl cp` without a restart.

**Why personas as data**: lets the personas be tuned (or replaced entirely for specific deployments) without rebuilding the container. Resists the temptation to encode persona-specific logic in C# — if a persona needs different model tier or different tools, encode that in frontmatter and let the workflow read it.

**Every persona takes a position.** `PersonaStep.PositionProtocol`, appended in code to every persona's system prompt (operator-written ones included), makes each view:
- open with a committed `**Position:**`;
- argue only from that persona's lens, leaving the trade-offs to the synthesis;
- refuse the middle option unless the lens itself favours it;
- name what the likely consensus would get wrong;
- close with `**Would change my mind:**`.

Each persona file says which question its position answers (the ethicist: what is right by the least powerful; the skeptic: which option it would bet against; and so on). The critique addendum keeps the position unless a sibling's argument defeats it on the persona's own terms.

Why: when each persona was asked to be balanced, each did the council's balancing on its own. On three contested questions in a row, all personas landed on the same compromise. The decision model rated the views as agreeing (p ≤ 0.009), so critique was skipped, and the synthesis had no real disagreement to integrate.

## Selector

A single `IChatClient` call (Balanced tier) that takes the question + the persona library and returns:

```json
{
  "personas": [
    { "id": "skeptic",   "needs_research": false },
    { "id": "economist", "needs_research": true  },
    { "id": "engineer",  "needs_research": true  }
  ],
  "pre_research": false,
  "critique": true,
  "rationale": "Question is strategic and contested; engineering feasibility and cost matter; pre-research not needed because personas can fetch as needed."
}
```

`pre_research` and `critique` are both auto-decided based on question type:
- **Pre-research on** when multiple personas would benefit from the same factual base (e.g., questions about specific technologies, companies, recent events).
- **Critique on** when the question is contested, strategic, or has plausible disagreement among personas. Off when the question is mostly factual integration.

The selector is **load-bearing** — its decisions drive cost and quality. Treat it as a component that needs its own eval set (see Risks).

## Output schema

```json
{
  "question": "...",
  "personas": [
    {
      "id": "skeptic",
      "view": "markdown prose, ~300 words",
      "key_points": ["...", "..."],
      "sources": []
    }
  ],
  "tensions": [
    {
      "between": ["skeptic", "economist"],
      "description": "Skeptic warns of lock-in; economist sees the cost-of-delay as higher.",
      "stakes": "Whether to commit now or pilot first."
    }
  ],
  "synthesis": "markdown prose — integrated guidance, ~500 words",
  "confidence": "low | medium | high",
  "assumptions": ["What the advice assumed about anything the caller did not say."],
  "metadata": {
    "critique_run": true,
    "pre_research_run": false,
    "persona_count": 3,
    "duration_ms": 87234,
    "model_calls": 9,
    "clarification": "not_asked | answered | declined"
  }
}
```

Returned as an MCP `CallToolResult`:

1. Text content: the `synthesis` prose, with a coverage banner first when personas are missing
   and the `assumptions` listed after it — readable by prose-consuming callers (primary agent, UI).
2. `structuredContent`: the full object above — for programmatic callers.

## Workflow stages

### 1. Select

Input: the question and its decision brief (`context`, `options`, the enums).
Output: `SelectorOutput` (above), plus `missing` (choice fields that would change the plan),
`too_thin` and `needs`. The caller's overrides (forced personas, `critique` / `pre_research`
on or off) are applied in code after parsing.
Model: Balanced tier.
Tools: none.
Failure mode: if selector output fails schema validation, retry once with stricter prompt; if it fails again, fall back to a default persona set (`skeptic`, `engineer`, `long_term`) with `critique=false`, `pre_research=false`.

### 2. PreResearch (conditional)

Skipped unless `SelectorOutput.pre_research = true`.
One call to the research server's MCP `research` tool (`ResearchMcpClient`; A2A to `ResearchAgent` before #717) with the original question and the selected lenses as `context`, timeout ~90s.
Result stored to working memory at `council/{taskId}/preresearch` and injected into every persona branch's context.

### 3. Fan-out

Parallel MAF branches, one per selected persona. Two branch shapes:

- **Call-only** (`needs_research=false`): single `IChatClient` call with `{system: persona.systemPrompt, user: question + optional pre-research}`. Returns persona view text.
- **Agentic loop** (`needs_research=true`): `AgentLoopRunner.RunAsync` with:
  - `MaxToolIterationsOverride = 8` (tight)
  - Tools: research (`ResearchMcpClient`, MCP → research server; A2A `ResearchAgentInvoker` before #717), `working_memory_get/list` scoped to `council/{taskId}/`
  - Findings cached to `council/{taskId}/{personaId}/` and visible to subsequent personas through the shared namespace
  - Before a research call runs, the decision model (`ResearchReuse`, purpose `CouncilResearchReuse`) checks whether research the run already holds — the shared pre-research and earlier calls — answers the new question. Above p ≥ 0.7 that finding is returned instead, with no new research run and no budget spent. The model off or unsure, the call goes ahead.

All branches use the High tier (`Council:PersonaTier`), for the view and again for the critique: the personas' reasoning is what the council sells. Until #758 every step got the plain `IChatClient`, which is Balanced, so personas, critique and synthesis all ran on Balanced whatever this document said. Each step now gets its tier's client from `TieredChatClientRegistry` (`Council:SelectTier` Balanced, `PersonaTier` and `SynthesisTier` High). Per-persona soft timeout 180 s (`Council:PerPersonaTimeoutSeconds`), room for a view that makes one research call; on timeout the persona is dropped from the council with `view: "(timed out)"`.

### 4. Critique (conditional)

Skipped unless `SelectorOutput.critique = true`. Select decides that from the question alone, so once the views exist the decision model (`CritiqueGate`, purpose `CouncilCritique`) gets one look at them: critique is skipped when it is fairly sure they already agree (it runs at p(disagree) ≥ 0.35), and `metadata.critique_skipped` says so. A caller's `critique: on` always runs it; with the model off or failing, critique runs as selected.
For each persona, a second `IChatClient` call: `{system: persona.systemPrompt + critique addendum, user: original question + all sibling views, instruction: "revise your view, name explicit disagreements"}`. Returns revised view + identified tensions.

Critique is **per-persona-parallel** like fan-out — no further fan-in needed.

### 5. Synthesize

Single `IChatClient` call (High tier), input: question + final persona views + identified tensions, output: structured JSON matching the output schema. Schema-validated; on validation failure, one repair pass before giving up.

## MCP surface

The council is an MCP server (#758): one tool, `advise`, at `http://advisor-council:8080/mcp`
(stateless streamable HTTP with the Tasks extension). The A2A agent card, its `advise` skill and
the never-implemented `rb-council-*` headers are gone; their overrides are tool arguments now.

| Argument | Type | Purpose |
|---|---|---|
| `question` | string, required | The decision or idea to deliberate on. |
| `context` | string | Goals, constraints, stakeholders, what has been tried. |
| `options` | string[] | The options being weighed. |
| `time_horizon` | `weeks` · `months` · `years` · `decade_plus` | How far ahead it matters. |
| `risk_appetite` | `low` · `moderate` · `high` | How much downside is acceptable. |
| `reversibility` | `easily_reversed` · `costly_to_reverse` · `irreversible` | How hard it is to undo. |
| `personas` | string[] | Force the persona set. |
| `critique` / `pre_research` | `auto` · `on` · `off` | Override the selector. |

The Select step may ask one multiple-choice question about what is still open, or return early
(`needs_context`) when the question is too thin to deliberate on. The rules are in
`design/advisor-council-mcp.md`, "Clarifying questions".

## Project layout

```
src/RockBot.AdvisorCouncil/
  RockBot.AdvisorCouncil.csproj
  Program.cs                         # ASP.NET Core MCP server (/mcp, /health)
  CouncilTools.cs                    # the `advise` MCP tool, clarifying question, result
  CouncilCallMode.cs                 # task vs plain call (copy of ResearchCallMode)
  CouncilClarificationToken.cs       # sealed requestState for MRTR retries
  Council/
    CouncilOrchestrator.cs           # ICouncil: SelectAsync + DeliberateAsync
    CouncilConcurrencyGate.cs        # Council:MaxConcurrentRuns
    SelectStep.cs
    PreResearchStep.cs
    PersonaStep.cs
    CritiqueStep.cs
    SynthesizeStep.cs
  Personas/
    Persona.cs                       # record { Id, Name, Description, SystemPrompt, DefaultResearch }
    PersonaRegistry.cs               # load + hot-reload from PVC
  Schema/
    CouncilRequest.cs                # the tool's arguments + the decision brief
    SelectorOutput.cs
    CouncilResponse.cs
  Tools/
    ResearchMcpClient.cs             # MCP client for the research server (was ResearchAgentInvoker, A2A)
  NullFeedbackStore.cs
  agent/
    personas/
      skeptic.md
      ethicist.md
      engineer.md
      economist.md
      long_term.md
      user_advocate.md
    directives.md

deploy/
  Dockerfile.advisor-council         # aspnet runtime, like Dockerfile.research-agent
  k8s/advisor-council.yaml           # Deployment + ClusterIP Service, like research-agent.yaml

tests/
  RockBot.AdvisorCouncil.Tests/
    SelectStepTests.cs               # fake IChatClient
    AdviseArgumentsTests.cs          # arguments, Select overrides, assumptions
    AdviseToolEndToEndTests.cs       # the tool over real HTTP: MRTR, tasks, concurrency
    PersonaRegistryTests.cs          # hot-reload, frontmatter parsing
    CouncilResponseSchemaTests.cs    # serialization round-trips
  RockBot.Agent.Tests/McpBridge/
    AdvisorCouncilBridgeTests.cs     # the tool behind the bridge with "tasks": true
```

## Deployment

- Image: `rockylhotka/rockbot-advisor-council:<version>`, tagged from `Directory.Build.props` `<Version>`. Never `:latest`.
- Kubernetes: `deploy/k8s/advisor-council.yaml` — a single-replica `Deployment` (`Recreate`)
  behind a ClusterIP `Service` `advisor-council:8080`, probes on `/health`. It replaced the KEDA
  `ScaledJob` (one pod per task), its queue-init job, the `rockbot.AdvisorCouncil.*` queues and
  the KEDA RabbitMQ secret in #758; the manifest's header has the one-time cleanup commands.
- Personas ship in the image at `/app/agent/personas` (`Council__PersonasPath`); point it at a
  mounted path for operator-customized personas.
- In-memory task store: a pod restart loses in-flight councils, which callers see as lost tasks.
  `Council__MaxConcurrentRuns` (default 2) caps deliberations per pod.

## Phased delivery

Each phase is one PR, mergeable on its own.

**Phase 1 — Scaffolding + baseline pipeline**
- Project, csproj, Program.cs, A2A wiring mirroring ResearchAgent.
- `PersonaRegistry` with frontmatter parsing + hot-reload.
- All 6 personas as markdown.
- MAF dependency added with pinned prerelease version.
- `CouncilWorkflow` with Select → Fan-out (no research) → Synthesize.
- Critique and pre-research wired but always off.
- Unit tests with fake `IChatClient` covering: selector output parsing, persona registry, schema serialization.

**Phase 2 — Cross-critique**
- `CritiqueStep` implementation, conditional MAF edge from selector.
- Selector prompt updated to decide `critique`.
- Output `metadata.critique_run` populated.

**Phase 3 — Research integration**
- `ResearchAgentInvoker` (AIFunction wrapping A2A invocation of `ResearchAgent`).
- Per-persona branch shape: agentic loop via `AgentLoopRunner` when `needs_research=true`.
- Pre-research stage conditional on selector output.
- Shared working-memory cache at `council/{taskId}/`.
- Integration test against real LLM (env-gated).

**Phase 4 — Deployment**
- `Dockerfile.advisor-council`.
- Helm chart additions for ephemeral pod pattern.
- PVC init container.
- Build/push workflow updates if applicable.

**Phase 5 — Primary-agent integration**
- Tool/skill in the primary `RockBot.Agent` that invokes the council via A2A.
- Directive snippet showing when to consult the council (consequential decisions, contested questions, before durable commitments).

**Later — MCP (#717, #758).** Research moved to MCP first (#717); then the council itself became
an MCP server with richer arguments and retired its A2A path (#758). See
`design/advisor-council-mcp.md`.

## Risks and open questions

**MAF prerelease churn.** Packages are `--prerelease` as of writing. Pin exact versions in `Directory.Build.props`. Treat MAF version bumps as their own PR. The workflow API surface specifically is the part most likely to change — if it breaks too often in Phase 1, fall back to hand-rolled orchestration without abandoning the rest of the design.

**Cost ceiling.** 5 personas × possible research × possible critique × synthesis can hit $0.50–$2 per question, more now that personas and critique run on High. Mitigations: tight per-persona iteration cap (8), hard wall-clock timeout (`Council:OverallTimeoutSeconds`, 600 s), per-request token budget enforced at the runner level, observable cost metrics published per council run.

**Selector reliability.** The selector decides which personas participate, whether to run research, and whether to critique. A bad selector silently degrades every downstream stage. Mitigation: a focused eval set of ~20 hand-graded questions covering factual, strategic, contested, and trivial cases. Re-run on selector prompt changes.

**Persona drift.** Personas are markdown on the PVC, so they can drift between deployments. Mitigation: hash personas at startup and include the hash in `metadata.persona_set_hash`. Easy to diff across runs.

**No persistence between council runs.** Each council task is independent; the council doesn't remember prior questions or its prior advice. This is intentional for v1 (simpler, no privacy concerns). If we want continuity later (e.g., "you advised X last time, has anything changed?"), we'd add a council-scoped memory namespace — out of scope here.

**Personas-as-characters temptation.** It will be tempting to make personas richer ("the skeptic is grumpy", "the long-term thinker is poetic"). Resist. Personas are framings; their job is to surface a specific class of consideration, not to entertain. Style belongs in the synthesis step if anywhere.
