# Observation Framework

## Problem

The agent maintains "theory of self" and "theory of user" markdown files that are intended to evolve over time as the agent observes its own behavior and the user's. In practice, they only evolve when the user explicitly asks the agent to update them — there is no recurring process driving accumulation, so the files stagnate and never reflect actual ongoing observation.

More broadly: the agent has no general mechanism for *accumulating evidence-based observations over time about anything*. Skills are recallable knowledge, memory is recallable facts, but neither serves the role of "observations that compound across many interactions, get reinforced or fade, and graduate into stable knowledge." That is the gap this design fills.

The two theory-of files are the first concrete consumers, but the framework is intended to be reused for additional observational domains as they emerge.

## Goals

- Theories (and other observational artifacts) evolve continuously, not on-demand.
- Observations are grounded in concrete evidence (quotes, conversation references), not generative LLM confabulation.
- The mechanism is general: new observational domains can be added by configuration, not by code.
- Single observations never become facts. Evidence must accumulate across multiple dreams before promotion.
- Stale observations age out without manual intervention.
- Cost-tiered: cheap LLM for high-recall extraction, expensive LLM only for judgment.
- Parallelizable from day one. Dream cycle runtime is already a concern; new phases must not make it worse.

## Non-goals

- Eliminating the LLM. Extraction and evaluation are LLM tasks; only bookkeeping is deterministic.
- Real-time observation. This is dream-cycle work, not per-turn.
- Self-editing of always-loaded directives. The framework owns regeneration; the agent does not edit its own theory files.
- Solving broader dream-cycle parallelism. That is a separate effort. The new phase introduced here parallelizes *within itself*.

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
    public int CandidateAgingWindowDreams { get; init; }
    public int TheoryAgingWindowDreams { get; init; }
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
  ]
}
```

References (conversation id + turn id + quote) are the load-bearing piece. They are what makes "promote when seen N times" honest — N distinct conversations, not N paraphrases of one.

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
   - Drop candidates with no new references in the last *K* dreams.
   - Drop or demote theories with no new supporting references in the last *M* dreams (M > K).
5. Regenerate the markdown file from the JSON state via a pure template. No LLM polish — that re-introduces the rewriting risk.

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
- LLM client (`ILlmClient`)
- Vector embedding service (already present for hybrid search)

It does *not* depend on messaging. The dream cycle is the only invoker. Agent code's role is limited to:
- Registering observation targets at startup (DI configuration).
- Loading the regenerated markdown files into agent context (this already happens for the existing theory-of files; nothing changes there).

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
- **Aging window defaults.** At 2 dreams/day, K=14 (one week) for candidates feels right; M=60+ for theories. Should be reviewed once there's data.
- **Evaluation prompt structure.** Differential framing is the right shape, but the exact prompt template needs iteration once Phase 1 is producing real candidate pools.
- **Telemetry surfacing.** Should the framework emit an end-of-phase summary message ("X candidates added, Y promoted, Z aged out per target") so dream activity is visible in logs without inspecting JSON files?
