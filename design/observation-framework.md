# Observation Framework

## Problem

The agent maintains "theory of self" and "theory of user" markdown files that are intended to evolve over time as the agent observes its own behavior and the user's. Today these are seeded by the portable prompts in `docs/getting-started-rockbot.md` ("Maintain a 'theory of self'..."), which ask the agent to write narrative self-models in its own voice. In practice, they only evolve when the user explicitly asks the agent to update them — there is no recurring process driving accumulation, so the files stagnate and never reflect actual ongoing observation.

More broadly: the agent has no general mechanism for *accumulating evidence-based observations over time about anything*. Skills are recallable knowledge, memory is recallable facts, but neither serves the role of "observations that compound across many interactions, get reinforced or fade, and graduate into stable knowledge." That is the gap this design fills.

The two theory-of files are the first concrete consumers, but the framework is intended to be reused for additional observational domains as they emerge.

This design **supersedes** the portable-prompt approach in the getting-started doc. Once the framework lands, the agent no longer hand-writes narrative theories — the framework accumulates structured, evidence-grounded observations, and the markdown files are deterministically regenerated from that data each dream. The getting-started doc will be updated to reference the framework instead of teaching the prompt pattern.

### Research-first framing

This is a science experiment: it has no *closed loop yet* in the sense of pushing structured theories into agent reasoning, but theories ARE published to the host's long-term memory store so they participate in `SearchMemory` and the hybrid-search index. The agent will surface a theory only when its query happens to match — there is no always-loaded injection into the system prompt, no evaluator that interrogates them, no rule promotion. Markdown copies of the state are also written to `{agentProfile}/observation/` for human inspection. Operators read those directly; the agent reaches theories only through normal memory recall. This deliberate scope keeps theories observable (and hybrid-searchable) without forcing them into every turn, so we can evaluate the data layer's quality before committing to any tighter closed loop. See "Closed loops (future)" below.

This framing affects design priorities: data integrity beats narrative readability. Better to have rigorous, evidence-grounded structured observations than a flowing self-portrait that may be partly confabulated. Synthesis can be layered on later if a closed loop demands it.

## Goals

- Theories (and other observational artifacts) evolve continuously, not on-demand.
- Observations are grounded in concrete evidence (quotes, conversation references), not generative LLM confabulation.
- The mechanism is general: new observational domains can be added by configuration, not by code.
- Single observations never become facts. Evidence must accumulate across multiple dreams before promotion.
- Stale observations age out without manual intervention.
- Cost-tiered: cheap LLM for high-recall extraction, expensive LLM only for judgment.
- Parallelizable from day one. Dream cycle runtime is already a concern; new phases must not make it worse.
- Historical visibility: snapshots of the regenerated markdown are retained so evolution over time is observable without external tooling.

## Non-goals

- Eliminating the LLM. Extraction and evaluation are LLM tasks; only bookkeeping is deterministic.
- Real-time observation. This is dream-cycle work, not per-turn.
- Self-editing of always-loaded directives. The framework owns regeneration; the agent does not edit its own theory files.
- Solving broader dream-cycle parallelism. That is a separate effort. The new phase introduced here parallelizes *within itself*.
- **Narrative synthesis in v1.** The existing getting-started prompt asked the agent to write its theories in narrative form ("themes you see recurring," "tensions or contradictions you've noticed"). v1 explicitly rejects this in favor of structured, evidence-grounded observations. Synthesis-on-top is a future possibility (see "Closed loops") but not part of v1.
- **Always-loaded context injection.** v1 does NOT auto-load the regenerated theory markdown into the agent's system prompt. Theories surface to the agent only when its own queries happen to match them via `SearchMemory` / hybrid search.
- **Evaluators, rules, self-monitoring loops.** No subsystems consume the theory data programmatically. v1 publishes for visibility; future work decides what to do with it.

## Closed loops (future)

v1 ships with one minimal closed loop: each promoted theory is published to the host's `ILongTermMemory` so the agent's existing `SearchMemory` tool and hybrid-search index can surface it on relevance. There is no always-loaded injection, no programmatic consumer of the data, and no rule promotion — theories only influence behaviour to the extent that the agent's own queries happen to match them. This section records the additional closed-loop options under consideration for later phases.

**Loop A — load markdown into context.** Inject the regenerated theory markdown into the agent's system prompt every turn (similar to how soul/directives/style are injected today). Theories would be visible every turn rather than only on relevance match. Costs: token budget per turn, requires plumbing into the profile loader.

**Loop B — publish theories as searchable memory entries (✅ shipped in v1).** Each promoted theory is written via `ILongTermMemory.SaveAsync` with category `observation/theory/{target.Name}`, importance 0.7, tagged with `observation` and the target name. The framework owns the lifecycle: insert on promote, delete on age-out. JSON state remains the source of truth; memory entries are derived. Memory writes are best-effort — JSON commit happens first, then memory writes — so a memory failure leaves a recoverable state.

**Loop C — targeted feed into specific subsystems.** Theories become structured input to specific decision points where they could plausibly help. Examples:
- `theory-of-user` → completion evaluator: "would this response satisfy this user given the observed patterns?"
- `theory-of-self` → mid-loop self-check: a Low-tier query like "given the observed pattern that I tend to over-explain, is this draft response in that pattern?"

Higher leverage than A. Each integration point is its own change.

**Loop D — promote high-confidence theories into rules/directives.** A theory reinforced over many conversations across many weeks could graduate to an actual rule the agent must follow. Strongest closed loop, highest risk: the agent's behaviour shifts based on its own observations of itself. Almost certainly needs human approval before promotion (a "review surface" where promotions are queued for the operator to approve, not auto-applied).

The decision to add loops A, C, or D will follow real evidence about what the v1 data layer actually produces. If theories surface usefully via `SearchMemory` (loop B), loop A may be unnecessary. If queries don't naturally reach them, loop A is a candidate. Loops C/D wait on their own evidence.

## Background: why not just edit directives?

An earlier proposal was a "learned-directives.md" file the agent could self-edit, always loaded into context. That was rejected because the always-loaded + self-editable + unbounded combination violates "nothing trusts the LLM" more than skills do — skills are scoped recall-on-demand, while a self-authored directive file becomes the agent's own constitution. For habits the designer can name today, editing the human-authored `directives.md` is the cleaner answer. The observation framework is for habits and traits the agent *discovers* through evidence, where the framework — not the LLM — owns the data and the promotion logic.

## Architecture overview

```
       Dream cycle
       │
       ▼
 ┌────────────────────────────────────────────────────────────┐
 │  ObservationPhase  (one phase in DreamService)             │
 │                                                            │
 │  parallel(targets):                                        │
 │    ┌──────────────────────────────────────────────────┐    │
 │    │  Per target:                                     │    │
 │    │                                                  │    │
 │    │  Phase 1: Extract + Merge                        │    │
 │    │    parallel(conversations):                      │    │
 │    │      Low-tier LLM extracts observations          │    │
 │    │      with quote citations                        │    │
 │    │    join: embed each observation, cluster         │    │
 │    │      against existing candidates (vectors),      │    │
 │    │      deterministically merge counts + refs       │    │
 │    │                                                  │    │
 │    │  Phase 2: Evaluate + Promote + Age + Regen       │    │
 │    │    Higher-tier LLM evaluates candidates that     │    │
 │    │      crossed the promotion threshold             │    │
 │    │    Promote → theories list (deterministic)       │    │
 │    │    Age out unreinforced candidates and theories  │    │
 │    │    Regenerate markdown from JSON via template    │    │
 │    └──────────────────────────────────────────────────┘    │
 └────────────────────────────────────────────────────────────┘
```

JSON is the source of truth. Markdown is regenerated each cycle as a deterministic template render.

## Configured targets (initial)

Two targets at launch. Each is an independent instance of the same pipeline.

### theory-of-self

- **Input filter**: all turns from the conversation log since the last dream.
- **Augmentation**: structured behavior summary per conversation (tool call count, iteration count, retried-after-error, exceeded-budget, self-corrected) computed mechanically — not asked of an LLM.
- **Tag observations with trigger context** (conversation / scheduled / heartbeat). Otherwise the agent will conclude things about itself that are only true of one mode.
- **Promotion threshold**: higher (more turns produce noisier evidence).

### theory-of-user

- **Input filter**: turns where `Source == user`, plus the agent responses to them. Not the agent's intermediate tool calls or scheduled-task activity — the user never sees those, so they aren't user signal. "User-authored" rather than "user-initiated": replies inside an agent-started chain still count.
- **Augmentation**: none. Tool noise is irrelevant here.
- **Promotion threshold**: lower (each user turn is a deliberate human choice, denser signal per turn).

The two pipelines are independent and run in parallel.

## Per-target configuration shape

```csharp
public sealed class ObservationTarget
{
    public string Name { get; init; }
    public ITranscriptFilter Filter { get; init; }
    public string ExtractionPrompt { get; init; }
    public string EvaluationPrompt { get; init; }
    public string StateFilePath { get; init; }      // JSON
    public string OutputMarkdownPath { get; init; }
    public LlmTier ExtractionTier { get; init; }    // Low
    public LlmTier EvaluationTier { get; init; }    // Balanced/High
    public int PromotionThreshold { get; init; }    // distinct conversations
    public int CandidateAgingWindowDays { get; init; }
    public int TheoryAgingWindowDays { get; init; }
    public bool IncludeBehaviorSummary { get; init; }
}
```

## State file shape (JSON)

One file per target. Schema versioned for future migration.

```jsonc
{
  "schemaVersion": 1,
  "lastDreamAt": "2026-05-07T12:00:00Z",
  "candidates": [
    {
      "id": "cand_abc123",
      "text": "User reverts diffs that touch tests they did not ask about.",
      "clusterId": "clust_42",
      "count": 2,
      "firstSeen": "2026-04-22T11:00:00Z",
      "lastSeen": "2026-05-03T09:30:00Z",
      "references": [
        { "conversationId": "conv_001", "turnId": "turn_017", "quote": "..." },
        { "conversationId": "conv_044", "turnId": "turn_004", "quote": "..." }
      ]
    }
  ],
  "theories": [
    {
      "id": "thry_xyz789",
      "text": "User prefers terse responses with no trailing summaries.",
      "promotedAt": "2026-03-15T08:00:00Z",
      "lastReinforced": "2026-05-06T18:00:00Z",
      "sourceCandidateIds": ["cand_..."],
      "references": [ /* same shape */ ]
    }
  ],
  "snapshots": [
    {
      "takenAt": "2026-05-01T12:00:00Z",
      "markdown": "# Theory of self\n\n..."
    }
  ]
}
```

References (conversation id + turn id + quote) are the load-bearing piece. They are what makes "promote when seen N times" honest — N distinct conversations, not N paraphrases of one.

**Snapshots.** `snapshots` retains the last *N* (default 12) regenerated markdown bodies with timestamps so evolution over time is observable without external tooling. At the framework's typical cadence (twice daily), 12 snapshots cover roughly the last week of history. Configurable per target. A new snapshot is appended each dream when phase 2 regenerates the markdown; oldest entries are evicted to maintain the cap.

## First-run behaviour

No bootstrap or import: any existing `theory-of-self.md` / `theory-of-user.md` file at `OutputMarkdownPath` is simply overwritten on the first phase-2 regeneration. The K8s deployment's PVC backups preserve the prior content if it is ever needed.

Until candidates accumulate enough reinforcement to promote, the regenerated markdown will show "Theories (0)" with a candidate-observations section listing in-progress signals. This is the honest state — the framework has no validated theories yet because it hasn't observed enough conversations. The first promoted theories appear once the configured threshold (default: 3 distinct conversations) is met for some candidate.

## Markdown template format

The regenerated markdown is a deterministic template render of the JSON state. No LLM polish (per the design's anti-rewriting rule). Concrete shape:

```markdown
# Theory of self

_Generated by the observation framework on 2026-05-07 12:00 UTC.
Manual edits to this file will be overwritten on the next dream cycle._

## Theories (5)

### User prefers terse responses with no trailing summaries.

- **Reinforced:** 7 conversations
- **First observed:** 2026-03-15
- **Last reinforced:** 2026-05-06
- **Representative quote:** "...just give me the diff, I can read it..."

### Agent over-explores before acting on simple file edits.

- **Reinforced:** 4 conversations
- **First observed:** 2026-04-02
- **Last reinforced:** 2026-05-04
- **Representative quote:** "...you don't need to read three files for a one-line change..."

## Candidate observations (12)

_Observations seen but not yet reinforced enough to promote.
Threshold: 3 distinct conversations._

- **Agent retries failed bash commands without diagnosing the root cause.** (1 conversation, 2026-05-06)
- **User defers decisions when given more than 3 options.** (2 conversations, 2026-04-22 → 2026-05-03)
- ...

## History

_Last 12 snapshots retained in JSON state. View with `rockbot snapshots theory-of-self`._
```

The "representative quote" is the verbatim quote from the most recent reference, truncated to ~120 chars. This keeps the evidence visible in agent context without bloating the file.

The candidate-observations section is included specifically so the operator (and the agent reading this in context) can see what's accumulating. Hiding candidates would lose the texture of "the framework has noticed something but isn't sure yet."

## Pipeline mechanics

### Phase 1: Extract + Merge

Per conversation in the input set (parallel, bounded by the LLM gateway):

1. Format the conversation according to the target's filter and augmentation rules.
2. Low-tier LLM call with the target's extraction prompt. Output: a list of candidate observations, each with at least one verbatim quote from the input.
3. Validation: drop any observation whose claimed quote is not actually present in the source. This is the single biggest anti-hallucination lever.

After all per-conversation extractions complete, join (single-threaded, in-memory, fast):

4. Embed each observation (local vector model — free in production environments).
5. Cluster against existing candidate vectors. Match threshold is target-configurable.
6. For each match, increment count and append references. For non-matches, create a new candidate.
7. Slightly imperfect clustering is fine — Phase 2 sorts it out.

### Phase 2: Evaluate + Promote + Age + Regenerate

Per target (parallel across targets):

1. Identify candidates whose count has crossed the promotion threshold and that have not yet been promoted.
2. Higher-tier LLM call with the evaluation prompt. Differential framing: "for each candidate, is it grounded in the cited references? does it conflict with existing theories? should it be promoted, refined, or rejected?" Verification against fixed input is much harder to confabulate than open-ended generation.
3. Apply the LLM's verdicts deterministically: promoted candidates become theory entries (carrying their references); rejected ones are removed.
4. Aging (deterministic):
   - Drop candidates with no new references in the last *K* days (default 7).
   - Drop theories with no new supporting references in the last *M* days (M > K, default 30).
5. Regenerate the markdown file from the JSON state via a pure template. No LLM polish — that re-introduces the rewriting risk.
6. Append a snapshot of the regenerated markdown to `snapshots[]`, evicting the oldest entry if the cap is exceeded.

### Per-conversation extraction failure handling

If the Low-tier extraction call for one conversation in the batch fails (LLM error, timeout, malformed JSON, gateway-saturated), the framework **skips that conversation, logs the failure, and continues with the rest of the batch**. Rationale:

- Single-conversation failures shouldn't lose the entire dream's worth of observation work.
- The skipped conversation will be revisited on the next dream cycle (if it falls within the dream window).
- Per-conversation retry within a single phase risks burning the gateway's slot budget on a failing conversation; the dream cadence handles retry naturally.

If *all* extractions in a batch fail (suggesting a systemic problem — gateway down, LLM provider outage, malformed prompt), the phase logs the failure, skips merge/evaluation, and exits without writing JSON state. The next dream cycle retries from scratch with the same conversation window.

Per-conversation failure metrics (count, tagged by target) are emitted so a non-zero rate is visible.

### Cancellation atomicity

Dream cycles can be preempted at any time by the work-serializer (a user message arriving). When `ct` fires mid-pipeline:

- **Phase 1 in-flight extractions** abort via `ct` propagation through the LLM gateway. The merge step does not run; the candidate pool in JSON is **not modified**. All extraction work for the current dream is lost. Acceptable: the next dream picks up the same conversation window or a superset.
- **Phase 1 merge** is in-memory and fast; if `ct` fires after merge has started, it completes (cheap deterministic code, no LLM calls). The JSON state file is then written.
- **Phase 2 evaluation** aborts via `ct` propagation if cancelled before the Higher-tier LLM call completes. Promotion/aging/regen do not run. JSON state is not modified.
- **Phase 2 promotion + aging + regen** are deterministic and run as a single in-memory unit; once started, they complete even if `ct` fires (small cost, ensures markdown stays consistent with JSON). The new state and regenerated markdown are then written together.

The contract: **JSON state is written atomically per phase-target**. A target either has all of phase 1's work for the current dream applied or none of it; same for phase 2. Partial states are never persisted. The trade-off is that cancellation can lose up to one phase's worth of work per target, which is acceptable given the dream cadence.

Implementation note: writes use an atomic rename pattern (write to `<file>.tmp`, fsync, rename to `<file>`). Crash mid-write does not corrupt the canonical file.

## Anti-hallucination levers (summary)

The pipeline assumes the LLM will confabulate if given the chance. Defenses, in order of importance:

1. **Quote-grounding.** Every observation must cite a verbatim quote from the input. Mechanically validated. No quote = observation discarded before it enters the pool.
2. **Behavior-only, not motivation.** Observations describe observable actions, not inferred mental state. "User reverts test diffs" stays; "user values test stability" doesn't.
3. **Differential evaluation.** Phase 2 verifies candidates against existing theories rather than generating freely.
4. **Multi-conversation graduation.** Single instances don't promote. A hallucinated pattern would have to be hallucinated the same way in independent conversations, which is unlikely.
5. **Aging.** Anomalies that don't reinforce fade out automatically.

## Parallelism

Two levels:

- **Across targets**: each configured target runs the full pipeline in parallel with others.
- **Within a target, Phase 1**: per-conversation extraction calls run concurrently.

Joins (cluster-merge, evaluation-promotion-aging-regen) are deterministic and serial within a target, but are cheap.

Concurrency is not bounded by the framework itself — it is bounded by the LLM gateway (see "Dependency: LLM gateway" below). The framework dispatches without restraint and lets the gateway throttle.

## Dream cycle integration

Today, `DreamService.DreamAsync` runs ~15 phases serially with `ct.ThrowIfCancellationRequested()` between each so a user message can preempt. The observation phase slots in as one additional phase. Internal parallelism is the framework's concern; the dream cycle just calls into it.

The phase needs to run *after* memory consolidation has settled (so the latest reinforced memories are available if any prompt references them) but does not have a hard ordering relationship with most other phases. Identity reflection runs near the end of the existing pipeline — observation can sit near it.

This phase MUST flow `ct` through to every LLM call inside it. If any path uses `CancellationToken.None`, user preemption stops working for that path. See "Dependency: LLM gateway" for the discipline this imposes.

Broader dream-cycle parallelism (running multiple existing phases concurrently) is out of scope here. It is a separate refactor with real risk (concurrent writes to memory store, graph store, etc.) and is not required to land this feature.

## Project placement

New project: **`RockBot.Observation`**.

Dependencies:
- LLM client (`ILlmClient`) — extraction + evaluation calls.
- Vector embedding service (`IEmbeddingGenerator<string, Embedding<float>>`) — already present for hybrid search.
- Long-term memory (`ILongTermMemory`) — publishes promoted theories so they appear in `SearchMemory` and the hybrid-search index. v1 closed loop B.

It does *not* depend on messaging. The dream cycle is the only invoker. Agent code's role is limited to:
- Registering observation targets at startup (DI configuration).
- Nothing else. The framework is self-contained: agent context loading is unchanged in v1 (markdown is written for inspection but not auto-loaded), and theories reach the agent only via the existing `SearchMemory` tool and hybrid-search index.

## Schema evolution

`schemaVersion: 1` lives at the top of every state file. When a future change requires migration, the framework reads the version field and routes to a migration step before the pipeline runs. Cheap to add now, painful to retrofit.

## Dependency: LLM gateway

The observation framework's parallelism makes a global LLM rate-limit-handling and concurrency-capping layer a prerequisite. Without it, every concurrent extraction call risks 429s and every call site needs its own retry logic. See [`llm-gateway.md`](llm-gateway.md) for that design. Key contract from the framework's perspective:

- The framework dispatches LLM calls without restraint; the gateway throttles via per-tier concurrency caps.
- Cancellation propagates: when dream's `ct` fires, every dream LLM call currently waiting at the gateway aborts.
- Every LLM call from the framework MUST flow `ct`. Any path using `CancellationToken.None` becomes a black hole where user preemption fails.

## Open questions

- **Behavior summary metric set.** What mechanically computed metrics are most useful for theory-of-self extraction? Tool call count and iteration count are obvious; what else carries signal without dragging in raw transcripts?
- **Exact placement in `DreamService.DreamAsync`.** After memory consolidation, before or after identity reflection? The existing phase order has accumulated reasons that should be reviewed.
- **Promotion threshold defaults.** Two distinct conversations? Three? Different per target. Calibrate against real dream output, not by guessing up front.
- **Aging window defaults.** Calendar-time aging in days makes the behaviour cadence-independent. Initial defaults: K=7 days for candidates, M=30 days for theories. Should be reviewed once there's data on real reinforcement frequency.
- **Evaluation prompt structure.** Differential framing is the right shape, but the exact prompt template needs iteration once Phase 1 is producing real candidate pools.
- **Telemetry surfacing.** Should the framework emit an end-of-phase summary message ("X candidates added, Y promoted, Z aged out per target") so dream activity is visible in logs without inspecting JSON files?
- **Closed-loop sequencing.** v1 ships with loop B (memory publish) only. The decision on whether/which of loops A, C, D to build follows real evidence about what the data layer produces — a rough trigger condition would help: "if after 2 months of accumulation theories surface usefully via `SearchMemory`, loop A may be unnecessary; if they're rarely surfaced naturally, loop A is the next step; loops C/D wait on their own evidence."
