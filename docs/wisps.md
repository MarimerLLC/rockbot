---
title: Wisps
nav_order: 10
---

# Wisps

Wisps are a **lightweight execution tier** for procedural, multi-step tasks that don't
require full agent context. Where subagents receive the complete `AgentContextBuilder`
pipeline (soul, directives, memory recall, skill index, knowledge graph) on every LLM
round-trip, wisps are harness-supervised pipelines that execute steps with minimal (or
zero) LLM involvement.

**Token savings:** A 5-step subagent task consumes ~60-80K input tokens. The same
workflow as a wisp costs ~2-12K tokens or less, because direct steps bypass the LLM
entirely and LLM steps receive only a tightly-scoped prompt.

---

## When to use wisps vs. subagents

| Wisps | Subagents |
|-------|-----------|
| Steps are known in advance | Task requires discovery/improvisation |
| Mostly tool calls with known parameters | Heavy judgment or multi-turn reasoning |
| Data pipelines: fetch → transform → output | Open-ended research or analysis |
| 2-12K tokens total | 60-80K tokens acceptable |

**Rule of thumb:** If you can write out the exact steps and parameters in advance,
use a wisp. If the task requires exploration or adaptation, use a subagent.

---

## Architecture

```
Calling agent (primary or subagent)
┌─────────────────────────────────────────────────────────┐
│                                                         │
│  LLM calls spawn_wisps(definitions: [{ ... }])          │
│     │                                                   │
│     ▼                                                   │
│  SpawnWispsExecutor.ExecuteAsync()                      │
│     │  Parses WispDefinition[] from JSON                │
│     │  Generates batch ID + per-wisp IDs                │
│     │  Runs wisps concurrently (semaphore-gated)        │
│     │                                                   │
│     ▼                                                   │
│  WispExecutor.ExecuteAsync()                            │
│     │  For each step:                                   │
│     │    ├── Direct: GatewayRouter → IToolExecutor      │
│     │    │   (zero LLM tokens)                          │
│     │    └── Llm: AgentLoopRunner.RunAsync()            │
│     │        (minimal context, Low tier)                │
│     │                                                   │
│     ▼                                                   │
│  Returns WispExecutionResult (synchronous)              │
│     │  Per-step success/failure + content                │
│     │  Failure classification (structural/external/…)    │
│     │  Working memory namespace for detailed output      │
│                                                         │
│  [calling agent receives structured result immediately]  │
└─────────────────────────────────────────────────────────┘
```

Unlike subagents (which are asynchronous — they return a task ID and deliver results
later via messaging), wisps are **synchronous**. The calling agent blocks until all
steps complete and receives the full result in the tool response.

---

## Step modes

### Direct mode

The harness invokes a tool with the exact parameters provided in the step definition.
No LLM is involved — zero token cost. Use for all deterministic operations where you
know the tool name, server, and parameters in advance.

### Iterated direct steps (`for_each`)

A direct step can run once per element of a JSON array that an earlier step returned
(#779). The harness does the iterating, so a data-dependent fan-out (search mail, then
fetch each message) costs zero LLM tokens instead of an LLM step making N tool calls or
a subagent.

```json
{
  "id": "fetch",
  "mode": "Direct",
  "gateway": "Mcp",
  "tool": "mail__get_message",
  "for_each": "{{steps.search.result.messages}}",
  "as": "msg",
  "params": { "accountId": "{{msg.accountId}}", "messageId": "{{msg.id}}" },
  "max_items": 25,
  "concurrency": 4,
  "on_item_failure": "continue"
}
```

| Field | Default | Meaning |
|-------|---------|---------|
| `for_each` | — | One `{{steps.<id>.result[.path]}}` template naming an earlier step; must resolve to a JSON array |
| `as` | — | Alias for the element: `{{msg}}` / `{{msg.id}}` alongside `{{item}}` / `{{item.id}}` |
| `max_items` | `ForEachDefaultMaxItems` (25) | Most elements allowed, clamped to `ForEachMaxItemsCeiling` (100). A longer list fails the step (Data) before any element runs |
| `concurrency` | 1 | Elements in flight at once, clamped to `ForEachMaxConcurrency` (4) |
| `on_item_failure` | `abort` | `abort` stops launching elements at the first failure and fails the step; `continue` runs the rest and succeeds if at least one element succeeded |

- Validation, MCP resolution, the schema check, pre-flight fills and auto-correction run
  once, on the step as written; the `{{item...}}` placeholders don't change the params'
  keys. The step is then routed, gated by the consequential-action gate, and invoked per
  element.
- `for_each` works on Mcp, Script and Web direct steps. `ForEachStepValidator` rejects it on
  an LLM step, and on an A2A step: `invoke_agent` refuses a dispatch while the session has an
  agent task pending, and a dispatch returns before the remote task finishes, so every item
  after the first would be refused. It also rejects a source that isn't an earlier step, a bad
  `as`, and the iteration fields or `{{item}}` (in params, message or prompt) on a step without
  `for_each`. All are Structural.
- A call that throws counts as a failed call of that item, so a failed write is still counted
  toward `WRITES FAILED`.
- Cancellation stops launching items, waits for the running ones, and fails the step with the
  items' outcomes and calls kept, so completed writes are still reported.
- Exceeding `max_items` fails rather than truncates: running the first N would leave the rest
  undone while the step read as complete.
- An empty array succeeds with `[]` and makes no calls.
- Under `abort`, elements already running finish (their writes happened and are reported);
  elements not yet started are marked skipped.
- The elements run inside the one wisp, which already holds a batch slot, so a batch has at
  most `MaxConcurrentWisps × ForEachMaxConcurrency` element calls in flight.
  `WispDispatchCircuitBreaker` counts wisp dispatches; elements don't add to it.
- Only one call per element. A multi-step loop body, or a loop inside a loop, is iterated by
  the parent agent across `spawn_wisps` batches.

### LLM mode

A lightweight LLM receives a tightly-scoped prompt (wisp directives + step instruction
+ data from prior steps) and makes tool calls. The wisp LLM has **no agent context** —
no soul, no memory, no conversation history, no skill index, no directives. It receives
only:

- ~200-token wisp directives (execute steps, call only listed tools, stop on error)
- The step prompt (provided by the calling agent)
- Data from `input_from` (injected into the prompt or chunked into working memory)
- **Auto-injected prior step results** — when `input_from` is not specified, all prior
  successful step outputs are automatically appended under a `## Prior Step Results`
  section (truncated to 4K chars per step, with full content available in working memory)
- Current date/time and timezone (injected by `AgentLoopRunner`)
- A scoped tool set (see Tool scoping below)

The LLM step always runs at `ModelTier.Low` with no follow-up pass and no completion
evaluator.

---

## Gateways

Direct mode steps route through one of four gateway types, each mapping to a specific
registered tool:

| Gateway | Registered tool | Required fields |
|---------|----------------|-----------------|
| `Mcp` | the typed `{server}__{tool}` wrapper, else `mcp_invoke_tool` | `tool` (typed name), or `server` + `tool`; `params` |
| `A2A` | `invoke_agent` | `agent`, `skill`, `message` |
| `Script` | `execute_{language}_script` | `params.script` |
| `Web` | `web_search` or `web_browse` | `tool`, `params` |

The `GatewayRouter` builds the correct `ToolInvokeRequest` arguments for each gateway
type.

**MCP steps use typed tools (#647).** A step names its tool by the typed name from the
agent's tool list (`"tool": "ms365__search_emails"`), or by `server` plus the server's own
tool name (`"server": "ms365", "tool": "search_emails"`). `WispExecutor` resolves either
form through `IMcpToolDirectory`; typed names are looked up, never parsed. `spawn_wisps`
rewrites the shorthand to `server` + `tool` before hashing, so both forms of the same wisp
share a definition hash and shape hash.

- A tool with a typed wrapper is called through the wrapper with its own arguments:
  `ms365__search_emails({ ... })`. In pinned and lazy mode the wrapper isn't in the
  registry, so the directory supplies its executor.
- A tool with no wrapper (a name over 64 characters, a sanitised-name collision, every
  tier `Off`), or one the directory doesn't know yet, falls back to
  `mcp_invoke_tool({ server_name: "ms365", tool_name: "search_emails", arguments: { ... } })`.
- A shorthand name that doesn't resolve within `McpReadinessWait` fails as a Structural
  authoring error.

Both paths end in `McpManagementExecutor.InvokeDownstreamAsync`, so recovery, elicitation
and pinning behave the same.

---

## Tool scoping

LLM steps can only call tools that are explicitly in scope. The tool set is built from:

1. All tools the **direct steps** call. An MCP step contributes its typed tool
   (`calendar-mcp__get_calendar_events`), with that tool's own schema. `mcp_invoke_tool`
   joins only when a referenced MCP tool has no typed wrapper.
2. Entries in the top-level **`tools`** array (for tools only LLM steps need): a registry
   tool name (`web_browse`), a typed MCP tool name, or an MCP server name, which brings in
   all of that server's typed tools.
3. **Working memory tools** (`GetFromWorkingMemory`, `SearchWorkingMemory`, etc.) —
   always available

This prevents the LLM from calling tools the wisp definition didn't anticipate.

The small wisp model gets MCP tools typed, never just the generic `mcp_invoke_tool`. #618's
measurements put weak models at 0–25% first-call correctness on the generic tool against
90–100% on typed ones. Wisp tools are caller-scoped (`ICallerScopedTool`), so the agent
loop keeps them even though LLM steps run at `ModelTier.Low`, which isn't eager. For the same
reason the run gets no MCP orientation.

### Tool drift

When a wisp is attached to a skill as a `Wisp` resource, the manifest entry records the
fingerprint of each MCP tool it calls, in `ToolFingerprints`, keyed `server/tool`. Every
attach path records it: eager promotion, the dream wisp-success promotion,
`promote_skill_asset` and repair-ticket attaches.

The dream service's wisp tool drift pass (`Dream:WispToolDriftEnabled`, no LLM call) compares
each validated wisp's fingerprints with the live tools:
- A tool whose fingerprint moved, or which is gone from a server the agent has indexed,
  sends the wisp back to provisional.
- The pass tags the description `[tool changed: server/tool]` and restarts the validation
  window (`CreatedAt`).
- The provisional validation pass re-promotes the wisp only after successes on the new
  surface, and then re-records the fingerprints.
- Unknown tools (server not indexed, fingerprint unavailable) are skipped.

---

## Data flow

### Step output storage

Every step's output is **always** written to working memory at
`wisp/{wispId}/{stepId}/output` (60-min TTL), regardless of whether `output_to` is set.
This ensures LLM steps can access prior step results via working memory tools.

A `for_each` step's output is a JSON array with one entry per element, in input order:

```json
[
  { "index": 0, "ok": true, "result": { "id": "m1", "subject": "..." } },
  { "index": 1, "ok": false, "error": "Message not found", "category": "Data" },
  { "index": 2, "ok": false, "skipped": true }
]
```

`result` is the call's output as its JSON value when it parses as JSON (objects, arrays,
numbers, booleans, null, strings), and a string otherwise. That array is the step's output
and its `output_to` file. It is written to `wisp/{wispId}/{stepId}/output` even when the step
fails. Items are not written to working memory one by one: `HybridCacheWorkingMemory` caps each
`wisp/{wispId}` namespace (`MaxEntriesPerNamespace`, default 50), and a large fan-out would
push out the aggregate and later steps' outputs.

### `output_to`

When present on any step (direct or LLM), the harness **additionally** writes the
step's result to a file on the shared volume:

- **Direct steps:** Tool response content is written to the file path
- **LLM steps:** LLM output text is written to the file path

### `input_from`

When present on a step, the harness resolves input data before the step executes:

- **Direct steps:** Template substitution in params (e.g., file path reference)
- **LLM steps:** The harness reads the file from the shared volume. Small content
  (≤8K chars) is injected directly into the prompt. Large content is chunked via
  `ContentChunker` into the wisp's working memory namespace, and the LLM receives
  an index table with chunk keys to retrieve via `GetFromWorkingMemory`.

When `input_from` is **not** specified on an LLM step, prior step results are
auto-injected (see LLM mode above).

### Template substitution

Step parameters and prompts support template references to prior step outputs:

- `{{steps.<id>.result}}` — Replaced with the step's output content
- `{{steps.<id>.result.a.b}}` — The value at that object path in the step's JSON output
- `{{steps.<id>.output_to}}` — Replaced with the step's `output_to` file path

In a `for_each` step, params also support:

- `{{item}}` / `{{item.a.b}}` — The current element, or a field inside it (object paths only)
- `{{<as>}}` / `{{<as>.a.b}}` — The same, under the step's `as` alias
- `{{item_index}}` — The element's zero-based position

Strings are inserted unwrapped; objects and arrays as their JSON text. A template that doesn't
resolve is left literal, so the downstream call fails visibly. All templates are resolved in one
pass, so text substituted from a step result or an element is never re-read as a template.

### Transition patterns

| Transition | Data path |
|-----------|-----------|
| Direct → Direct | Shared volume files (scripts read/write directly) |
| Direct → LLM | Harness reads file, chunks into working memory if large |
| LLM → Direct | Harness writes LLM output to shared volume |
| LLM → LLM | Working memory (no file round-trip) |

---

## Error handling

### On-failure branching

Direct steps support `on_failure` with two actions:

- `{ "action": "abort" }` — Stop the pipeline and return the error (default)
- `{ "action": "skip_to", "skip_to": "step_id" }` — Skip intermediate steps and
  resume at the named step

A `for_each` step that fails (an element failed under `on_item_failure: abort`, every element
failed under `continue`, a source that isn't an array, or too many elements) is a failed step
like any other, so `on_failure` applies to it. Its error names the first failed element:
`Item 3 of 10 failed (on_item_failure: abort; 3 succeeded, 6 not run): …`.

### Failure classification

Every failure is automatically classified by the harness:

| Category | Examples | Learnable? |
|----------|----------|-----------|
| **Structural** | Wrong tool name, missing params, bad gateway config | Yes — skill bug |
| **External** | Timeout, service unavailable, rate limit | No — transient |
| **Judgment** | LLM picked wrong result, missed key data | Partially — prompt quality |
| **Data** | Unexpected format, empty results, schema mismatch | Yes — assumption bug |

### Working memory lifecycle

On success, the wisp's working memory entries are given a **5-minute TTL** so the
calling agent can still inspect results briefly after completion. On failure, entries
retain their original 60-minute TTL for debugging — the calling agent can read the
namespace to inspect intermediate results.

---

## Parallel execution (batches)

The `spawn_wisps` tool accepts an array of definitions. Multiple wisps execute
**concurrently**, gated by `WispOptions.MaxConcurrentWisps` (default 10). Each wisp
is fully independent — no cross-wisp template references.

### Batch mechanics

- All wisps in a batch share a `batchId` for log correlation
- Each wisp gets its own ID and working memory namespace
- Failures in one wisp do **not** abort others — all wisps run to completion
- Total wall-clock time equals the slowest wisp, not the sum
- A JSON batch summary is written to working memory at
  `wisp/batch-{batchId}/summary` (60-min TTL) with per-wisp status and output

### Batch result format

The tool response includes per-wisp status with output previews (up to 2K chars):

```
5 wisp(s) completed (5 succeeded, 0 failed, 10.7s total):

- `wisp-abc`: "Search marimer emails" [ok] (1180ms)
  Output: ...
  Output key: `wisp/wisp-abc/search/output` (to filter it, pass this key to spawn_workers as input_keys; ...)
- `wisp-def`: "Search xebia emails" [ok] (2733ms)
  Output: ...
  Output key: `wisp/wisp-def/search/output` (...)
...

Batch ID: `batch-abc123def456ab`
Batch summary: `wisp/batch-batch-abc123def456ab/summary`
```

If any wisp failed, the result opens with `PARTIAL FAILURE: N of M wisps failed…`, or with
`ALL M WISPS FAILED…` when none succeeded. Each failed wisp lists the steps that completed
before the failure (#686). A wisp that `on_failure: skip_to` carried past a failed step still
counts as `[ok]`. The failed step is now listed under it (`Step create FAILED (handled by
on_failure…)`). When any such step's write failed, the result opens with `WRITES FAILED: N of M
state-changing calls failed…` and the call counts as failed. A `for_each` step that succeeded
under `on_item_failure: continue` with failed elements counts the same way: its line reads
`Step fetch: 2 of 10 items FAILED (on_item_failure: continue), indices 3, 7…`, and its failed
state-changing element calls count toward `WRITES FAILED`. A `for_each` step's output is
capped at 4,000 characters in the result, with the working-memory key for the full array.
The response stays a non-error, so the model doesn't re-run the wisps
that worked. The `spawn_wisps` call is still recorded as failed in the ledger and the tool-call
log, with the counts and every call the wisps' steps made: the one call of a direct step (an MCP
step named `{server}__{tool}`), and every substantive call of an LLM step. An LLM step whose
state-changing calls all failed is itself a failed step. See
[agent-host.md](agent-host.md#when-the-completion-evaluator-runs).

### Fetch once: handing wisp output on (#847)

Each successful wisp's line names its final step's output key. Its step outputs stay in
working memory for `WispOptions.SuccessOutputTtl` (30 minutes). That way the calling agent
can filter the data without fetching it again. A light filter is an `Llm` extract step at
the end of the wisp. Heavier judgment goes to `spawn_workers` with
`input_keys: ["wisp:<wisp-id>/<step-id>"]`, which gives the worker the data as input (see
[worker-subagents.md](../design/worker-subagents.md)).

Every successful Direct step is also recorded in the run's fetch registry
(`RunFetchRegistry`). It holds the tool and its arguments with keys sorted; a
`mcp_invoke_tool` call and the typed `{server}__{tool}` call count as the same tool. When
the agent, or a worker it spawned, later makes the same read-only call, the result gets a
system note naming the wisp step and its output key. The call still runs, and the note allows
a deliberate re-read for fresher data. A successful state-changing call on a server clears
that server's recorded reads, so re-reading after a write is not flagged; an MCP tool named
like the agent's own bookkeeping (`task_update`, `save_memory`) counts as such a write. Reads
meant to repeat are not tracked: names with `status`, `poll`, `progress`, `health`,
`current`, `now`, `time` or `wait`, and MCP discovery (`mcp_list_services` and the like). A wisp
step is cited only while its output key can still be in working memory (the shorter of
`SuccessOutputTtl` and the 60-minute step TTL). A call with no key is cited only to the loop
that made it, since another loop (a worker, the spawner) cannot see that result.

### When to parallelize

Split independent work into separate wisp definitions. For example, searching 5
email accounts should be 5 wisps (one per account), not 5 sequential steps in one
wisp. Each wisp can still have its own multi-step pipeline internally (e.g.,
Direct fetch → LLM summarize).

### Per-wisp logging

Each wisp in a batch produces its own `WispExecutionRecord` with the shared `BatchId`
field for correlation. Retry detection works per-wisp via definition hashes.

A wisp with `for_each` steps also records `IteratedSteps`: per step, the element count,
how many succeeded, failed and were skipped, and the first 10 failed indices. The field is
omitted for wisps without `for_each`, so their JSONL lines are unchanged.

The `for_each` fields are part of both hashes. The definition hash serializes them, and the
shape hash includes the source template, `as`, `max_items`, `concurrency` and
`on_item_failure`. Both leave them out when unset, so wisps that don't iterate keep the
hashes they had before the fields existed.

---

## Failure tracking (Phase 4)

Every wisp execution (success or failure) produces a persistent `WispExecutionRecord`
written to `IWispExecutionLog` (JSONL-backed via `FileWispExecutionLog`). Records include:

- Wisp ID, description, and a SHA-256 definition hash
- Success/failure status with step-level detail
- Failure classification and error message
- Session ID for correlation
- `RetryOf` link when the wisp is detected as a successful retry of a prior failure

### Correction pair capture

When a wisp succeeds and a prior failure with the same definition hash exists in the
same session, `SpawnWispsExecutor` links them as a correction pair and emits a
`WispCorrection` feedback signal via `IFeedbackStore`. The signal includes the prior
failure's category, error message, and failed step — giving the dream system structured
evidence of what went wrong and how it was fixed.

---

## Dream-time learning (Phase 5)

The `DreamService` includes a `RunWispFailureAnalysisPassAsync()` pass that:

1. Queries the last 14 days of wisp execution records
2. Groups by description to identify recurring patterns
3. Sends a summary to the LLM with a structured analysis directive
4. The LLM returns:
   - **Failure patterns** — recurring failures with category, frequency, and
     recommendations
   - **Skill updates** — annotations to append to existing skills (negative examples,
     corrected patterns)
   - **Promotion candidates** — consistently successful wisp patterns that could become
     stored wisp skills (Phase 6, deferred)

Configuration: `DreamOptions.WispFailureAnalysisEnabled` (default `true`) and
`DreamOptions.WispFailureDirectivePath` (default `wisp-failure-dream.md`).

### Wisps run by scheduled tasks (#848)

The success pass reads the last 7 days of wisp runs, but a weekly task makes one run a week and
would never reach the frequency threshold. Runs from a scheduled task's `patrol/{task}` session
are read over `DreamOptions.ScheduledWispSuccessLookback` (default 35 days) instead.

Promotion counts distinct runs, not records. That normally means distinct sessions, but every run
of a scheduled task shares one session id, so a patrol wisp could never validate. For a
`patrol/` session each UTC day the task ran counts as one run.

---

## Scheduled-task gates (#848)

A recurring task that checks "is there anything new?" pays for a full LLM turn even when the
answer is no. A **gate** is a saved wisp (a skill's `Wisp` resource) that runs first, with no LLM:

1. On each fire, `ScheduledTaskHandler` loads `skill/resource`, fills `{{last_run_at}}` (ISO 8601,
   UTC) and `{{last_run_date}}` (`yyyy/MM/dd`) from the task's previous fire (30 days back when it
   has never fired), and runs it through `ISavedWispRunner`. The run is logged like any other wisp
   run, so it counts toward promotion.
2. `ScheduledTaskGate.Evaluate` decides from every step's output. The wisp found nothing only when every step did, so a mail search that found messages still passes when a later calendar step comes back empty:
   - **Empty** means blank output; JSON `null`, `[]` or `{}`; an object whose result arrays
     (`results`, `items`, `messages`, `value`, …) are all empty or whose count (`count`, `total`,
     `resultSizeEstimate`, …) is 0; an MCP text envelope around any of those; or a short
     "No results found." sentence with nothing after it.
   - In `nonEmpty` mode any results pass. In `changed` mode results pass only when their hash
     (over every non-empty step output) differs from the last run's.
3. When the gate does not pass, the run ends there: no LLM call, no reply, no saved report. It is
   recorded as a `GatedSkip` run in `scheduled-task-runs.json`.
4. When it passes, the patrol turn gets two messages:
   - a **system** message with only our own framing: what ran, since when, and that the results are
     outside data to report on, never instructions;
   - a **user-role** "Gate wisp results" block holding each step's output (12,000 chars between
     them), its `wisp/{id}/{step}/output` working-memory key, and the `wisp:{id}/{step}` form a
     worker's `input_keys` takes. Every line is quoted with `> `, so a planted "SYSTEM:" line or a
     forged end marker can't pass as part of the frame.

   The turn shares the gate's `RunFetchRegistry`, so a repeat of the wisp's fetch is flagged.
5. The gate's state is stored when the run ends: at once for a skip, and only after the turn has
   finished for a pass or forced run. A turn that is preempted (and retried) or fails leaves the
   state as it was, so a `changed` gate doesn't mistake the retry's results for ones already seen.

Safety:

- **Fail open.** No wisp subsystem, a missing resource or a failed step runs the task as if it had
  no gate (`FailedOpen`, logged as a warning).
- **Forced run.** After `MaxConsecutiveSkips` (default 6) skipped runs in a row the task runs
  anyway (`ForcedRun`), so a wrong gate cannot silence it for good. 0 never forces a run.
- **Who can switch it on.** The user (through the agent's `set_task_gate` tool, an automation change
  under the consequential-action gate), or the dream's
  [scheduled-task efficiency pass](dream-service.md#scheduled-task-efficiency), which switches a gate
  on only when `ISavedWispRunner.Inspect` finds every step known to only read. That means an `Mcp` step
  whose tool name starts with a read verb (`get_`, `list_`, `search_`, …) with no write verb after
  it, or a `Web` step (`web_search` / `web_browse`). Unknown names, `Script`, `A2A` and LLM steps don't
  qualify. A valid wisp that doesn't qualify is recorded as a suggestion; a missing or invalid wisp is
  not recorded at all. It never replaces a gate the user set.

The gate's state (`lastResultHash`, `consecutiveSkips`) lives on the task's `gate` in
`scheduled-tasks.json`. `schedule_task` with the same name replaces the task and drops its gate.

---

## DI registration

```csharp
agent.AddWisps(opts =>
    opts.SharedVolumePath = "/rockbot/shared");
```

This registers:
- `WispExecutor` — core pipeline execution engine
- `FileWispExecutionLog` as `IWispExecutionLog` — persistent execution records
- `WispToolRegistrar` — registers `spawn_wisps` in the tool registry
- `WispToolSkillProvider` — provides `get_tool_guide("wisp")` documentation

The `spawn_wisps` tool is available to both the primary agent and subagents (source
`"wisp"` passes the subagent tool filter).

---

## Project structure

```
src/RockBot.Wisp/
├── WispDefinition.cs          # Top-level pipeline definition
├── WispStep.cs                # Single step (id, mode, gateway, params, etc.)
├── StepMode.cs                # Direct / Llm enum
├── GatewayType.cs             # Mcp / A2A / Script / Web enum
├── OnFailureAction.cs         # abort / skip_to
├── FailureCategory.cs         # Structural / External / Judgment / Data
├── WispExecutor.cs            # Core pipeline executor
├── WispExecutionResult.cs     # Overall result with per-step detail
├── WispStepResult.cs          # Single step result
├── WispStepError.cs           # Error with classification
├── GatewayRouter.cs           # Maps steps to tool invocations + template resolution
├── ForEachStepValidator.cs    # Structural checks for for_each steps
├── WispRegistryToolFunction.cs # AIFunction wrapper for scoped LLM step tools
├── SpawnWispsExecutor.cs      # IToolExecutor for spawn_wisps tool
├── WispBatchResult.cs         # Batch result with per-wisp outcomes
├── WispToolRegistrar.cs       # IHostedService tool registration
├── WispToolSkillProvider.cs   # IToolSkillProvider usage guide
├── WispOptions.cs             # Configuration (SharedVolumePath, MaxConcurrentWisps)
├── WispServiceCollectionExtensions.cs # AddWisps() DI extension
├── FileWispExecutionLog.cs    # JSONL-backed execution log
│
src/RockBot.Host.Abstractions/
├── IWispExecutionLog.cs       # Execution log interface
├── WispExecutionRecord.cs     # Persistent execution record
│
tests/RockBot.Wisp.Tests/      # 71 unit tests
```

---

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `WispOptions.SharedVolumePath` | `/rockbot/shared` | Root directory for shared volume file I/O |
| `WispOptions.MaxConcurrentWisps` | `10` | Max wisps executing concurrently in a batch |
| `WispOptions.SuccessOutputTtl` | `00:30:00` | How long a successful wisp's step outputs stay in working memory (was a fixed 5 minutes) |
| `WispOptions.ForEachDefaultMaxItems` | `25` | Elements a `for_each` step may iterate when it sets no `max_items` |
| `WispOptions.ForEachMaxItemsCeiling` | `100` | Hard ceiling on `max_items` |
| `WispOptions.ForEachMaxConcurrency` | `4` | Hard ceiling on a `for_each` step's `concurrency` (default 1) |
| `DreamOptions.WispFailureAnalysisEnabled` | `true` | Enable wisp failure analysis dream pass |
| `DreamOptions.WispFailureDirectivePath` | `wisp-failure-dream.md` | Custom directive for the dream pass |
