# Worker Subagents

## Problem

Patrol runs take ~10 minutes wall-clock. Tracing one heartbeat-patrol cycle shows the cost is not in MCP tools (each `list_accounts`, `get_email_details`, `get_calendar_events` finishes in well under a second) but in **LLM round-trips**. The three patrol subagents — active-plans, email-scan, calendar-patrol — each make ~20–25 LLM turns, and every turn re-ingests the full context the primary agent ships them: long-term memory recall, episodic memory, identity entries, knowledge graph triples, full soul/directives/style/memory-rules system prompt.

For a focused gather task ("list 6 calendar accounts, fetch events for each, summarize") none of that context is load-bearing. The subagent is mechanically iterating MCP calls. The LLM's job is to interpret tool results and decide whether to branch — it does not need persona, episodic recall, or a knowledge graph.

Four reusable observations underlie this:

1. **`SubagentRunner` routes through the primary-agent context builder.** `AgentContextBuilder.BuildAsync` is shared with `UserMessageHandler` and `ScheduledTaskHandler` and injects the full menu (LTM, episodic, identity, KG, skills, service hints, working memory, conversation history, model guardrails). The subagent path passes no override beyond `systemPromptOverride` and `workingMemoryNamespace`.
2. **Subagents inherit the primary's tool surface.** `SubagentRunner` filters out `subagent`/`scheduling`/`a2a` sources and the MCP server-management tools, but every remaining tool — `save_memory`, `save_skill`, `promote_skill_asset`, `update_task_directive`, full registry — is exposed. The tool schemas themselves consume meaningful prompt budget on every turn.
3. **Subagents inherit the primary's reasoning scaffolding.** `AgentLoopRunner` injects iteration-budget guidance, step-by-step planning prompts, and post-loop completion re-prompting. For a gather subagent this manifests as "did I really finish?" deliberation rounds that extend the loop past the point where the data is collected.
4. **The ladder has a missing rung.** `spawn_wisps` covers deterministic tool sequences (no LLM). `spawn_subagent` covers open-ended deliberation (full LLM). There is no rung for "needs an LLM to interpret tool results and branch, but does not need to think." That rung is where ~80% of patrol work lives.

## Goals

- A `spawn_workers` tool (plural, array input matching `spawn_wisps`) that runs a lean LLM loop per worker with a slim system prompt, no LTM/episodic/identity/KG injection, a trimmed tool surface, and no completion re-prompting.
- Workers pin to Low-tier model routing and are not eligible for High-tier escalation.
- Workers return a typed result — confirmed facts, the working-memory key written, items blocked, converged tool-call patterns observed — so the spawning agent does not LLM-parse free-form prose.
- Workers cannot spawn any kind of nested agent. Subagents *may* spawn workers (workers stay leaf nodes; the spawning surface is primary-agent + subagent).
- The primary agent and subagents receive clear directive guidance on `spawn_workers` vs `spawn_subagent` selection.
- Patrol subagents migrate to workers where applicable; the patrol directive is updated to prefer workers for gather tasks.

## Non-goals

- Removing `spawn_subagent`. Deliberative, persona-bearing, or open-ended tasks remain full subagents. Workers are the additive lean rung.
- Eliminating wisps. Workers complement wisps — wisps stay the right answer when steps are deterministic.
- A universal cost model. Workers are a structural change; per-task tier routing and prompt tuning remain orthogonal.
- Retroactive migration of historical subagent traces. Workers are new code paths; old subagent records remain subagent-shaped.

## Architecture overview

```
                  ┌──────────────────────────────────────────────┐
                  │       Primary agent (or scheduled task)       │
                  └────────┬──────────────────────────┬──────────┘
                           │                          │
                  spawn_wisps               spawn_workers        spawn_subagent
                  (no LLM)                   (lean LLM)          (full LLM)
                           │                    │                       │
                           ▼                    ▼                       ▼
                    ┌────────────┐       ┌──────────────┐        ┌──────────────┐
                    │ WispExec   │       │ WorkerRunner │        │ SubagentRunner│
                    │ direct MCP │       │ slim ctx     │        │ full ctx      │
                    │ no LLM     │       │ trimmed tools│        │ full tools    │
                    └────────────┘       │ Low tier     │        │ all tiers     │
                                         │ no re-prompt │        │ re-prompt OK  │
                                         └──────┬───────┘        └──────┬───────┘
                                                │                       │
                                                ▼                       ▼
                                         typed WorkerResult       free-form SubagentResult
                                         (facts, keys, blocked)    (output text)
                                                │                       │
                                                └──────────┬────────────┘
                                                           ▼
                                          ┌──────────────────────────────┐
                                          │ Primary agent: synthesis     │
                                          │ Reads working memory keys,   │
                                          │ writes shared/patrol/*-latest│
                                          └──────────────────────────────┘
```

## Worker contract

`spawn_workers` accepts an array of `WorkerDefinition` objects, executes them concurrently (gated by `WorkerOptions.MaxConcurrentWorkers`), and returns a batch result. Per-worker inputs:

- `description` — what to do, in one sentence. ("Scan all calendar-mcp accounts for events May 21–28; save events plus actionable next-24h items to `shared/patrol/calendar-latest`.")
- `context` — optional handoff string from the spawning agent. Pre-resolved facts the worker should treat as given (e.g., "active accounts are X, Y, Z").
- `result_key` — optional override for the working-memory key the worker writes its structured output to. Default is auto-assigned `worker/<task-id>/result` and echoed back in the receipt. Override is used when the spawning agent wants the worker to overwrite a known shared key directly (e.g., `shared/patrol/calendar-latest`).
- `timeout_minutes` — required-with-default soft cap on wall-clock. Default 5 minutes. Required-not-optional so the spawning agent must think about the expected duration rather than fall through to a generic ceiling.
- `tools_allow` — optional explicit allowlist of MCP server / tool prefixes the worker may invoke. Defaults to "any MCP data tool plus working memory, file I/O, spawn_wisps, and report_progress." Bounds the worker to a single domain when the spawning agent knows the scope.

### Tool surface

Always present:
- `mcp_invoke_tool`, `mcp_get_service_details`, `mcp_list_services`
- `spawn_wisps` (workers can delegate deterministic sub-sequences to wisps)
- Working memory tools scoped to the worker's namespace (`worker/<task-id>/...`)
- `report_progress`
- `file_read` / `file_write` against the shared volume

Never present:
- `spawn_workers`, `spawn_subagent` (workers are leaf nodes — closes fan-out blow-up; subagents may still spawn workers)
- `save_memory`, `save_skill`, `promote_skill_asset` (workers don't curate long-term state — they record converged patterns in their result for the spawning agent to promote)
- `update_task_directive` (workers don't write to scheduled-task playbooks)
- `invoke_agent` (A2A; replies fold into a different session, useless here)
- MCP server-management (`mcp_register_server`, `mcp_unregister_server`)

### Context build

A new `WorkerContextBuilder` (or `AgentContextBuilder.BuildForWorkerAsync`) injects only:

| Element | Worker | Subagent | Notes |
|---|---|---|---|
| System prompt | Slim `worker-soul.md` (~200 tokens) | Full soul + common-directives + style + memory-rules + subagent-directives | Biggest single token win |
| Datetime | yes | yes | Calendar/email work needs it |
| Active rules | yes | yes | Safety constraints |
| Model guardrails | yes | yes | Format/behavior |
| Skill index (one-shot) | yes | yes | Procedural knowledge — workers need MCP tool patterns |
| Skill BM25 recall | yes | yes | Cheap, load-bearing for tool calls |
| Service hints | yes | yes | Tells worker which MCP servers exist |
| Working memory (shared / worker ns) | yes | yes | Spawning agent's handoff lives here |
| Working memory (patrol / subagent ns) | no | yes (when user session) | Cross-session noise for workers |
| Long-term memory (BM25) | **no** | yes | Facts about the user — irrelevant to mechanical scans |
| Episodic memory | **no** | yes | Recent experiences — noise for fresh focused scan |
| Identity entries | **no** | yes | Workers are anonymous executors, not persona-bearing |
| Knowledge graph triples | **no** | yes (when KG configured) | Entity relationships don't help "list these 6 accounts" |
| Conversation history | no | no (empty) | Workers/subagents have no prior turns |

### `worker-soul.md` (sketch)

```
You execute a focused task. You are not the primary agent.

- Read the task description and any context the spawning agent provided.
- Use MCP tools to gather facts. Use spawn_wisps for deterministic
  multi-step sequences.
- Save structured findings to the working-memory key you were given.
- Call report_progress only when a step takes more than a few seconds
  or you hit a blocker — not for narration.
- When the task is done, return a one-line completion receipt. Do not
  summarise findings in the reply — the spawning agent reads them from
  working memory.

You do not deliberate about persona, history, or motivation. You execute,
save, and stop.
```

### Loop runner

A `WorkerLoopRunner` derived from `AgentLoopRunner` but with:

- **No completion re-prompting.** When the model returns no tool calls, the worker exits. No "are you sure you're done?" pass.
- **No reasoning scaffolding.** Skip the iteration-budget and step-by-step planning system messages.
- **Tighter iteration cap.** Default 12 turns (subagent default is much higher). Exceeding it is a hard failure.
- **Pinned model tier.** Low for v1 — one line, predictable, easy to revert. If post-migration we see workers failing on tasks that genuinely need Balanced (e.g. parsing complex email bodies), promote the default to "Balanced ceiling" but never enable High escalation. A worker that needs High is mismodeled — the spawning agent should have used a full subagent.
- **Overflow stash and metrics still apply.** Lean ≠ unsafe. Token/tool-call metrics record with `subagent_type=worker` so we can compare worker vs subagent cost in dashboards.

### Output contract

`WorkerResult`:

```csharp
public sealed record WorkerResult(
    string TaskId,
    bool IsSuccess,
    string ResultKey,                          // Auto-assigned worker/<task-id>/result, or the override the caller supplied
    int FactsRecorded,                         // Count of distinct facts saved
    IReadOnlyList<string> Blocked,             // Items the worker could not verify
    IReadOnlyList<ConvergedPattern> ConvergedPatterns, // Tool-call patterns the worker observed working — for spawning-agent promotion
    TimeSpan Duration,
    int LlmTurns,
    string? FailureReason);                    // Populated when IsSuccess == false
```

`spawn_workers` returns a batch JSON receipt — one `WorkerResult` per definition, not free-form summaries. The spawning agent reads `ResultKey` from each receipt and fetches the actual data from working memory.

**Failure surfacing** mirrors the subagent pattern exactly: when a worker fails or is cancelled, the runner publishes a `worker/<task-id>/failure-details` working-memory entry with the rolling progress buffer and the final error. Same shape, same TTL conventions, same observation/eviction machinery.

**Converged patterns and promotion.** Workers do not call `promote_skill_asset` directly — the lean LLM does not have the skill/identity context to write a good skill description. Instead, when a worker observes a tool-call sequence that converged on success (especially after one or more failed attempts), it records it in `ConvergedPatterns`. The spawning agent (which has full context) reads these on the synthesis pass and calls `promote_skill_asset` itself for any worth keeping. This keeps the asset-promotion loop from `skill-asset-promotion.md` closed without putting promotion in the wrong runner.

## Migration

### Phase 1: Build the rung

1. Add `IWorkerRunner` + `WorkerRunner` in `src/RockBot.Worker/` (new project) or under `src/RockBot.Subagent/Worker/` (shares the runner infrastructure).
2. Add `WorkerContextBuilder` (or extend `AgentContextBuilder` with a `BuildForWorkerAsync` overload) that injects only the slim set.
3. Add `worker-soul.md` to `src/RockBot.Agent/agent/` (and the init container copy chain).
4. Add `SpawnWorkersExecutor` registered under source `worker` in `IToolRegistry`. Register it in both the primary-agent tool set and the subagent tool set; filter it out only from the worker's own tool set (workers stay leaf nodes).
5. Add `WorkerResult`, `ConvergedPattern`, and the `worker/<task-id>/*` working-memory namespace (`/result`, `/failure-details`).
6. Tests: token-count regression against a fixed prompt fixture (proves the lean build is leaner); end-to-end worker run against a fake MCP server; nested-worker spawn rejection; subagent-spawning-worker happy path; tool-allowlist enforcement; auto-assigned vs override `result_key` behavior; converged-pattern surfacing on a forced-retry scenario.

### Phase 2: Update directives

1. Add a "Worker vs subagent" section to `src/RockBot.Agent/agent/common-directives.md` with a selection table and a worked example.
2. Add the same guidance to `subagent-directives.md` — subagents may spawn workers for mechanical sub-tasks and should prefer doing so over inlining the gather work.
3. Document the `convergedPatterns` review step: after a `spawn_workers` batch completes, the spawning agent reviews each receipt's `ConvergedPatterns` and calls `promote_skill_asset` for any worth keeping.
4. Update `heartbeat-patrol.md` to spawn workers for the calendar / email / active-plans scans.

### Phase 3: Migrate patrol

1. Update the heartbeat-patrol directive (via `update_task_directive`, not a file edit — the live directive is in `scheduled-tasks.json` on the PVC) to call `spawn_workers` once with three definitions for the calendar / email / active-plans scans.
2. Run one patrol cycle, compare wall-clock and LLM-turn count against a pre-migration baseline captured before phase 1 ships.
3. Repeat for `daily-operational-brief` and `evening-calendar-summary` evidence-gathering slices.

### Phase 4: Tighten the boundary

1. If the comparison shows workers consistently complete in under N turns, lower `MaxIterations` further.
2. If certain MCP tools are never called by workers, consider excluding them from the tool surface entirely to shrink schema-injection cost further.
3. If a class of "worker that needed deliberation" emerges, that is a signal to fall back to `spawn_subagent` for that case — not to add deliberation to workers.

## Open questions

- **Worker timeout default value.** The contract is decided (required-with-default, 5 min initial). The actual right default is empirical — after phase 3 we should reset it from observed P95 wall-clock.
- **Balanced-tier promotion threshold.** Workers pin to Low for v1. If a class of workers consistently fails on tasks that need Balanced (e.g. parsing complex email bodies), we promote the default tier ceiling. The trigger condition for that promotion — failure rate? specific category of failure? — needs definition once we have data.
- **Worker concurrency cap.** `WorkerOptions.MaxConcurrentWorkers` analogous to `MaxConcurrentWisps`. The patrol spawns three; what's a reasonable ceiling for the LLM-providers' rate limits and our own working-memory contention? Default to the existing wisp concurrency cap until we see otherwise.
- **Schema-injection cost.** Even after all the listed savings, MCP tool schemas account for a substantial fraction of every prompt. The `tools_allow` parameter exists to let the spawning agent restrict the schema surface, but defaults currently include every MCP data tool. If post-phase-3 measurements show schemas dominate, we narrow the default — open question is whether to make `tools_allow` required (forces explicit scoping) or keep it optional with a narrower default set.

## Risks

- **The primary agent picks the wrong type.** If the directive guidance is unclear, the primary spawns subagents for gather tasks and the lean-by-design rung sits unused. Mitigation: explicit selection table in `common-directives.md` plus a worked example. If post-migration patrol still spawns subagents for gather tasks, that is a directive problem, not an architecture problem.
- **Workers underperform on edge cases.** Without LTM/identity, a worker might miss a nuance the user established ("ignore the personal calendar during work hours"). Mitigation: such constraints belong in the active-rules list (which workers do receive) or the `context` string from the spawning agent. If active rules are not the right home for that constraint, the constraint is in the wrong place.
- **Two runners drift over time.** `WorkerRunner` and `SubagentRunner` share infrastructure but diverge in defaults. Mitigation: share the underlying `AgentLoopRunner` and gate the differences via an explicit `LoopProfile` ("Worker" vs "Subagent") on the loop options. One runner, two profiles.

## Success criteria

- Wall-clock for one heartbeat-patrol cycle drops by ≥40% versus the pre-migration baseline (target: from ~10 min to under 6 min).
- Median LLM turns per gather task drops from ~20–25 to ≤8.
- Worker P95 input tokens per turn drops by ≥50% versus subagent baseline.
- No regression in patrol fact-coverage — the post-migration `shared/patrol/heartbeat-latest` carries at least the same number of confirmed facts as the pre-migration baseline over a 7-day sample.
- Worker failure rate (no result written, or `IsSuccess=false`) under 5% over a 7-day sample.
