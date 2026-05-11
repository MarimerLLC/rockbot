# Advisor Council Agent

## Inspiration

The multi-persona deliberation pattern isn't new — it appears across the prompting community as "council of experts," "panel of advisors," and similar variants. The immediate prompt for building it into rockbot was JD Meier's "Council of Giants" post: https://www.linkedin.com/posts/jdmeier_council-of-giants-activity-7458556131225784321-JIkl

What this design adds is making the council a first-class agent in the rockbot swarm — A2A-invocable, persona library as data on the PVC, selector-driven gating, integrated with `ResearchAgent` for fact-finding — rather than a one-shot prompt template.

## Problem

The agent (and its users) regularly face open-ended questions where a single LLM pass — even a long one with tools — produces shallow or one-sided answers. "Should we adopt X?" "Is this design sound?" "What are we missing?" These benefit from being examined from multiple framings, not from running one bigger loop. Today rockbot has no structured way to do that: questions either get a single-perspective answer from the primary agent, or get punted to research (which surfaces facts, not judgment).

The deeper issue is that "wisdom" — the kind of thinking that surfaces tensions, names what's at stake, and integrates competing considerations — emerges from explicit perspective-taking, not from telling one model to "be wise." Multi-perspective reasoning needs to be a workflow, not a prompt.

## Goals

- Take a question or idea and return integrated, multi-perspective guidance with explicit tensions and confidence.
- Make the perspective set transparent and tunable (personas as data on the PVC, hot-reloaded).
- Let personas reach for facts when they need them, by invoking the existing `ResearchAgent` via A2A.
- Surface the council as an A2A skill the primary agent can self-invoke before its own consequential decisions.
- Keep cost and latency bounded with selector-driven gating (which personas, with/without research, with/without critique).

## Non-goals

- Replacing the primary agent's decision-making. The council returns guidance; the caller decides what to do with it.
- Persona personality theatre. Personas are framings, not characters — short, focused system prompts, no role-play.
- Real-time interaction. Council runs are batch (typically 30s–3min) and return a single structured result.
- Replacing `ResearchAgent`. The council reuses it for fact-finding; it does not duplicate web search.

## Architecture overview

```
                   ┌──────────────────────────────────────────────┐
A2A AgentTaskRequest │             AdvisorCouncilTaskHandler        │
   skill=advise     │  (one task per ephemeral pod, then shutdown) │
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
                              AgentTaskResult
                          (JSON part + text part)

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
  "metadata": {
    "critique_run": true,
    "pre_research_run": false,
    "persona_count": 3,
    "duration_ms": 87234,
    "model_calls": 9
  }
}
```

Returned as **two `AgentMessagePart`s** in the `AgentTaskResult`:

1. `kind=text` containing only the `synthesis` prose — readable by prose-consuming callers (primary agent, UI).
2. `kind=data` (or `kind=text` with a JSON content-type header) containing the full structured object — for programmatic callers.

## Workflow stages

### 1. Select

Input: question text.
Output: `SelectorOutput` (above).
Model: Balanced tier.
Tools: none.
Failure mode: if selector output fails schema validation, retry once with stricter prompt; if it fails again, fall back to a default persona set (`skeptic`, `engineer`, `long_term`) with `critique=false`, `pre_research=false`.

### 2. PreResearch (conditional)

Skipped unless `SelectorOutput.pre_research = true`.
Single A2A invocation of `ResearchAgent` with the original question, timeout ~90s.
Result stored to working memory at `council/{taskId}/preresearch` and injected into every persona branch's context.

### 3. Fan-out

Parallel MAF branches, one per selected persona. Two branch shapes:

- **Call-only** (`needs_research=false`): single `IChatClient` call with `{system: persona.systemPrompt, user: question + optional pre-research}`. Returns persona view text.
- **Agentic loop** (`needs_research=true`): `AgentLoopRunner.RunAsync` with:
  - `MaxToolIterationsOverride = 8` (tight)
  - Tools: `ResearchAgentInvoker` (A2A → ResearchAgent), `working_memory_get/list` scoped to `council/{taskId}/`
  - Findings cached to `council/{taskId}/{personaId}/` and visible to subsequent personas through the shared namespace

All branches use Balanced tier. Per-persona soft timeout ~60s; on timeout the persona is dropped from the council with `view: "(timed out)"`.

### 4. Critique (conditional)

Skipped unless `SelectorOutput.critique = true`.
For each persona, a second `IChatClient` call: `{system: persona.systemPrompt + critique addendum, user: original question + all sibling views, instruction: "revise your view, name explicit disagreements"}`. Returns revised view + identified tensions.

Critique is **per-persona-parallel** like fan-out — no further fan-in needed.

### 5. Synthesize

Single `IChatClient` call (High tier), input: question + final persona views + identified tensions, output: structured JSON matching the output schema. Schema-validated; on validation failure, one repair pass before giving up.

## A2A surface

Agent card:

```json
{
  "agentName": "AdvisorCouncil",
  "description": "Multi-perspective advisor council. Analyzes questions from a curated set of personas and returns integrated guidance.",
  "version": "1.0",
  "skills": [
    {
      "id": "advise",
      "name": "Advise",
      "description": "Take a question or idea and return multi-perspective analysis with synthesis."
    }
  ]
}
```

Request: standard `AgentTaskRequest` with the question as a `text` part. Optional headers:
- `rb-council-personas`: comma-separated persona ids to force-override selection
- `rb-council-critique`: `true|false|auto` (default `auto`)
- `rb-council-pre-research`: `true|false|auto` (default `auto`)

## Project layout

```
src/RockBot.AdvisorCouncil/
  RockBot.AdvisorCouncil.csproj
  Program.cs
  AdvisorCouncilTaskHandler.cs       # IAgentTaskHandler entry point
  Council/
    CouncilWorkflow.cs               # MAF graph builder
    SelectStep.cs
    PreResearchStep.cs
    PersonaStep.cs
    CritiqueStep.cs
    SynthesizeStep.cs
  Personas/
    Persona.cs                       # record { Id, Name, Description, SystemPrompt, DefaultResearch }
    PersonaRegistry.cs               # load + hot-reload from PVC
  Schema/
    SelectorOutput.cs
    CouncilResponse.cs
  Tools/
    ResearchAgentInvoker.cs          # AIFunction wrapping A2A → ResearchAgent
  EphemeralShutdownCoordinator.cs    # mirrors ResearchAgent
  EphemeralShutdownService.cs
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
  Dockerfile.advisor-council         # mirrors Dockerfile.agent
  helm/rockbot/templates/
    advisor-council-deployment.yaml  # ephemeral pod template, like research agent

tests/
  RockBot.AdvisorCouncil.Tests/
    SelectStepTests.cs               # fake IChatClient
    PersonaRegistryTests.cs          # hot-reload, frontmatter parsing
    CouncilResponseSchemaTests.cs    # serialization round-trips
    CouncilWorkflowIntegrationTests.cs  # gated by ROCKBOT_LLM_KEY env var
```

## Deployment

- Image: `rockylhotka/rockbot-advisor-council:<version>`, tagged from `Directory.Build.props` `<Version>`. Never `:latest`.
- Helm: new `advisorCouncil` block in `values.yaml` with `image.repository`, `image.tag`, `enabled`. Pinned tag.
- PVC: `/data/advisor-council/` with `personas/` subdir. Init container `cp -n` from image — never overwrites running pod's customized personas.
- One pod per task (ephemeral), `EphemeralShutdownService` exits cleanly after `AgentTaskResult` is published.

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

## Risks and open questions

**MAF prerelease churn.** Packages are `--prerelease` as of writing. Pin exact versions in `Directory.Build.props`. Treat MAF version bumps as their own PR. The workflow API surface specifically is the part most likely to change — if it breaks too often in Phase 1, fall back to hand-rolled orchestration without abandoning the rest of the design.

**Cost ceiling.** 5 personas × possible research × possible critique × synthesis can hit $0.50–$2 per question. Mitigations: tight per-persona iteration cap (8), hard wall-clock timeout (~3 min), per-request token budget enforced at the runner level, observable cost metrics published per council run.

**Selector reliability.** The selector decides which personas participate, whether to run research, and whether to critique. A bad selector silently degrades every downstream stage. Mitigation: a focused eval set of ~20 hand-graded questions covering factual, strategic, contested, and trivial cases. Re-run on selector prompt changes.

**Persona drift.** Personas are markdown on the PVC, so they can drift between deployments. Mitigation: hash personas at startup and include the hash in `metadata.persona_set_hash`. Easy to diff across runs.

**No persistence between council runs.** Each council task is independent; the council doesn't remember prior questions or its prior advice. This is intentional for v1 (simpler, no privacy concerns). If we want continuity later (e.g., "you advised X last time, has anything changed?"), we'd add a council-scoped memory namespace — out of scope here.

**Personas-as-characters temptation.** It will be tempting to make personas richer ("the skeptic is grumpy", "the long-term thinker is poetic"). Resist. Personas are framings; their job is to surface a specific class of consideration, not to entertain. Style belongs in the synthesis step if anywhere.
