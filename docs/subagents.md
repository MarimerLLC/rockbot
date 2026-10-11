---
title: Subagents
nav_order: 5
---

# Subagents

The subagent subsystem lets the primary agent delegate long-running or complex
tasks to isolated in-process LLM loops. The primary agent continues the
conversation normally while one or more subagents work in the background,
sending progress updates and a final result when done.

---

## Why subagents?

The primary agent's tool-calling loop has a finite iteration limit (default 12
round-trips). Tasks that require many sequential tool calls — deep research,
multi-step data processing, exploratory workflows — will hit this limit before
finishing. Scheduling a task (via `schedule_task`) works for deferred work but
blocks the user until the next fire time and runs in a fresh context with no
conversation awareness.

Subagents solve both problems:

- **No iteration cap** — each subagent runs its own loop with its own limit
- **Immediate** — spawned on demand, not deferred to a future cron window
- **Background** — the user can continue talking while the subagent works
- **Conversational** — progress and results arrive as messages in the active session

> **See also: [Wisps](wisps.md)** — for procedural multi-step tasks with known
> parameters, wisps provide a lighter-weight alternative that costs 80-95% fewer
> tokens. Use subagents when the task requires discovery or multi-turn reasoning;
> use wisps when you can write out the exact steps in advance.

---

## Architecture

```
Primary agent session
┌─────────────────────────────────────────────────────────┐
│                                                         │
│  User: "Research X and summarize"                       │
│     │                                                   │
│     ▼                                                   │
│  UserMessageHandler                                     │
│     │  LLM calls spawn_subagent(description, ...)       │
│     │                                                   │
│     ▼                                                   │
│  SubagentManager.SpawnAsync()                           │
│     │  Creates isolated DI scope                        │
│     │  Starts SubagentRunner as background Task         │
│     │  Returns task_id immediately                      │
│     │                                                   │
│  "Subagent spawned — task_id: abc123"                   │
│     │                                                   │
│  [conversation continues normally]                      │
└─────────────────────────────────────────────────────────┘

Subagent (background task, isolated DI scope)
┌─────────────────────────────────────────────────────────┐
│                                                         │
│  SubagentRunner.RunAsync()                              │
│     │  Builds system prompt + user turn                 │
│     │  Tools: long-term memory + working memory +       │
│     │         skills + registry + ReportProgress        │
│     │                                                   │
│     │  AgentLoopRunner.RunAsync()                       │
│     │     ├── LLM call → tool call → result → …         │
│     │     └── LLM calls ReportProgress("Found 3 items") │
│     │              │                                    │
│     │              ▼                                    │
│     │   Publishes SubagentProgressMessage               │
│     │         to "subagent.progress"                    │
│     │                                                   │
│     └── Final text response                             │
│              │                                          │
│              ▼                                          │
│   Publishes SubagentResultMessage                       │
│         to "subagent.result"                            │
│                                                         │
└─────────────────────────────────────────────────────────┘

Primary agent (message handlers)
┌─────────────────────────────────────────────────────────┐
│                                                         │
│  SubagentProgressHandler                                │
│     │  Receives SubagentProgressMessage                 │
│     │  Synthetic user turn:                             │
│     │    "[Subagent task abc123 reports]: Found 3 items" │
│     │  Runs full primary agent context + LLM loop       │
│     │  Publishes AgentReply (IsFinal=true)              │
│     │  → User sees: "Still working — found 3 so far"    │
│                                                         │
│  SubagentResultHandler                                  │
│     │  Receives SubagentResultMessage                   │
│     │  Synthetic user turn:                             │
│     │    "[Subagent task abc123 completed]: <output>"   │
│     │  Runs full primary agent context + LLM loop       │
│     │  Publishes AgentReply (IsFinal=true)              │
│     │  → User sees final summary                        │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

---

## Tools available to the primary agent

### `spawn_subagent`

Spawns a background subagent. Returns immediately with a `task_id`.

```
spawn_subagent(
    description,      // Detailed instructions for the subagent (required)
    context?,         // Additional data or context to pass in
    timeout_minutes?, // Max runtime — default 10 minutes
    max_iterations?,  // Tool-loop cap; research/synthesis tasks get at least 20
    consolidate?,     // Batch with sibling results (default true)
    inputs?           // Exact working-memory keys / shared-volume paths to build on
)
→ "Subagent spawned with task_id: abc123def456"
```

If the concurrency limit is reached, returns an error string starting with `"Error:"`.

`inputs` are checked at spawn. A key or path that does not exist fails the spawn with the
close matches that do (keys in the same namespace, the same key name under another task,
files with the same name), so a mistyped or copied key fails fast. Valid inputs are inlined
into the subagent's starting context ahead of other prior work.

A description that asks the subagent to research, investigate, verify, ground, synthesize,
outline or draft — or to write a deck or document — raises a `max_iterations` below
`SubagentOptions.ResearchIterationFloor` (20) to the floor, and logs it.

### `cancel_subagent`

Cancels a running subagent by task ID.

```
cancel_subagent(task_id)
→ "Subagent abc123def456 cancelled."   // or "No active subagent found…"
```

### `list_subagents`

Lists all currently running subagent tasks with elapsed time and description.

```
list_subagents()
→ Active subagents (1):
  - task_id=abc123def456, elapsed=23s, description=Research quantum computing…
```

---

## Tools available inside a subagent

These tools are injected directly into the subagent's `ChatOptions.Tools` with
`taskId` and `primarySessionId` baked in. They are not registered in the global
`IToolRegistry` and are not available to the primary agent.

Subagents also receive the full long-term memory tools (`SaveMemory`, `SearchMemory`,
`DeleteMemory`), working memory tools, and skill tools — the same set as the primary
agent, minus the subagent management tools (`spawn_subagent` etc.).

### `ReportProgress`

```
ReportProgress(message)
→ "Progress reported."
```

Publishes a `SubagentProgressMessage` to `subagent.progress`. The primary agent's
`SubagentProgressHandler` picks this up, builds the full primary-session context,
runs the LLM, and delivers a natural-language update to the user.

Call this periodically — after completing a significant step, not after every
tool call.

---

## Data handoff via working memory namespaces

Subagents write large outputs to their own working memory namespace (`subagent/{taskId}/`).
The primary agent reads from that namespace once the subagent completes. No extra tools or
infrastructure — it's the same `save_to_working_memory` / `get_from_working_memory` the
subagent uses for everything else.

**Namespace isolation:**

- **Subagent namespace:** `subagent/{taskId}/` — all `save_to_working_memory` calls from the
  subagent are stored here automatically (the namespace is baked in at tool construction)
- **Primary session namespace:** `session/{primarySessionId}/` — the primary agent's own scratch
  space; not written to by the subagent

**Usage pattern:**

```
Primary agent (before spawning):
  spawn_subagent("Scrape [url1, url2, url3] and summarize findings")

Subagent (runs in namespace "subagent/abc123"):
  [fetches urls]
  save_to_working_memory("url1_content", "...", ttl_minutes=240, category="scrape-result")
    → stored at "subagent/abc123/url1_content"
  save_to_working_memory("summary", "...", ttl_minutes=240)
    → stored at "subagent/abc123/summary"
  ReportProgress("Done. Results saved: url1_content, summary")

Primary agent (on result, via SubagentResultHandler):
  # SubagentResultHandler checks for entries in "subagent/abc123/" and includes a hint if found:
  # "[Subagent task abc123 completed]: ... Additional outputs were written to working memory.
  #  Keys: 'subagent/abc123/url1_content', 'subagent/abc123/summary'. Retrieve and present them."
  search_working_memory(namespace: "subagent/abc123")
  get_from_working_memory("subagent/abc123/summary")
```

**Why working memory instead of long-term memory:**

- Subagent outputs are temporary — they don't need to survive beyond the current conversation
- TTL (default 4 hours for subagent outputs) handles cleanup automatically
- No explicit cleanup required in `SubagentResultHandler` — entries expire naturally
- Cross-namespace reads are first-class in the working memory API, no workarounds needed

A `get_from_working_memory` miss under `subagent/<id>/` lists the keys that do exist under
that task — or, when the task has none, the same key name under another task and the recent
subagent namespaces.

### Prior work and the session work registry

Later subagents see what earlier ones produced without the primary passing it along. The
singleton `ISessionWorkRegistry` (RockBot.Host) keeps, per conversation (primary session):

- **Subagent results** — task id, description, a summary (the first ~600 characters of the
  output), the full output (capped), and the working-memory keys the subagent stored. Bulk
  web/tool chunk keys (`…-chunkN`, `…-index`) are counted, not listed. Recorded by
  `SubagentResultHandler` when the result arrives.
- **Artifacts** — shared-volume files written, edited, moved or deleted (`file_*` tools) and
  remote upload targets (any tool whose name contains `upload`, unwrapped from
  `mcp_invoke_tool` or `{server}__{tool}`), with the last writer. Recorded from the loop's
  tool-call ledger, so every tool call of every run passes the same hook. Subagent, worker and
  wisp sessions are linked to their primary at spawn, so their writes count toward it; wisp
  direct steps, which run outside any loop, record themselves.

The registry is in memory, bounded (20 results, 50 artifacts per conversation, 24-hour
expiry; `SessionWorkRegistryOptions`) and lost on restart — the keys and files it points at
outlive it.

`SubagentRunner` injects a **"Prior work in this conversation"** block into each new
subagent's context: the user request that led to the spawn, the inputs, every earlier
result (summary and keys) and every artifact, and then the full text of the most relevant
items up to `SubagentOptions.LineageInlineBudgetChars` (24,000). Inputs come first; results
are ranked by keyword overlap between the new task and each result's description and summary,
most recent first on a tie (with no overlap, only the most recent result is inlined). The
block tells the subagent to ground its work in these results and that research wins over an
older draft.

For user turns, `AgentContextBuilder` adds a short **"Work products in this conversation"**
section (≤1,500 characters) listing the files, uploads and result keys, so "the deck" resolves
to a concrete path.

---

## Message types

### `SubagentProgressMessage`

Published by subagent → handled by `SubagentProgressHandler` on primary side.

```csharp
public sealed record SubagentProgressMessage
{
    public required string TaskId { get; init; }
    public required string SubagentSessionId { get; init; }
    public required string PrimarySessionId { get; init; }
    public required string Message { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

Topic: `subagent.progress`

### `SubagentResultMessage`

Published by subagent on completion → handled by `SubagentResultHandler`.

```csharp
public sealed record SubagentResultMessage
{
    public required string TaskId { get; init; }
    public required string SubagentSessionId { get; init; }
    public required string PrimarySessionId { get; init; }
    public required string Output { get; init; }
    public required bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? BatchId { get; init; }
    public bool Consolidate { get; init; } = true;
    public string? OriginatingUserRequest { get; init; } // #666
    public string? Description { get; init; }            // #665
    public IReadOnlyList<SubagentToolCallSummary>? ToolCalls { get; init; } // #683
    public int? ToolCallCount { get; init; }             // #683
    public string? RunOrigin { get; init; }              // #685
    public string? UserAskedFor { get; init; }           // #685
}
```

`RunOrigin` and `UserAskedFor` are the consequential-action scope the subagent ran under (#685):
`SubagentManager` captures the spawning run's scope at spawn (`SubagentEntry.ActionGate`, origin
`subagent-of-user-turn` for a user turn) and `SubagentRunner` runs under it, so a subagent spawned
from an information-only user message cannot create events, send mail or upload files — the call
comes back "Not run: …" and the subagent reports the change as a proposal instead. Wisps and
workers it starts inherit the same scope. The synthesis turn relaying the result runs under it
too. See [Consequential-action gate](agent-host.md#consequential-action-gate).

`ToolCalls` is a compact copy of the subagent run's tool-call ledger: up to 40 calls, each with
its name, ok/failed, whether it changed state, and a short argument summary. `ToolCallCount` is
the total number of calls. The primary's synthesis turn passes both to the completion evaluator,
with the session-work-registry artifacts written by the subagent and its wisps. The evaluator
checks the relayed report's claims against what the subagent actually did, not against the
synthesis turn's own read-only calls. See
[When the completion evaluator runs](agent-host.md#when-the-completion-evaluator-runs).
Both properties are null on results from an older build.

A batch call such as `spawn_wisps` carries a `Detail` (`6 of 7 wisps failed`) and up to 15
`Nested` calls, which are the calls its wisps made, each with its own outcome (#686). Before this,
a batch whose wisps all aborted was relayed as one successful `spawn_wisps`. A subagent also runs
the evaluator on its own reply before the result is published. It runs only for the
`side-effect`, `bare-claim` and `claimed-change` triggers, so a claim like "seven events created
and verified" or "I updated the checklist" must be backed by the subagent's own calls.

Topic: `subagent.result`

---

## SubagentManager

`SubagentManager` is a singleton that owns the lifecycle of all running subagent
tasks.

**Spawn:** Checks the active count against `MaxConcurrentSubagents`. If under
the limit, generates a `taskId` (12-char hex), creates a linked
`CancellationTokenSource` (with `DefaultTimeoutMinutes` cap), and starts
`SubagentRunner.RunAsync` as a background `Task` in an isolated `IServiceScope`.
The entry is added to a `ConcurrentDictionary` and the `taskId` is returned
before the background task reaches its first `await`.

**Cleanup:** Completed tasks are pruned lazily from the active dictionary on
every call to `SpawnAsync` and `ListActive`. The `SubagentRunner` also calls
`_active.TryRemove` in its `finally` block.

**Cancel:** Signals the `CancellationTokenSource`, waits up to 5 seconds for
the task to finish, then removes the entry.

---

## SubagentRunner

`SubagentRunner` is registered as **transient** and resolved fresh from a new
`IServiceScope` per task — it is never shared between tasks or sessions.

Its `RunAsync` method:

1. Builds a focused system prompt explaining the subagent role
2. Optionally injects a `Context:` system message from the caller
3. Adds the task `description` as the first user turn (no prior history)
4. Constructs `ChatOptions.Tools`:
   - Long-term memory tools (`SaveMemory`, `SearchMemory`, `DeleteMemory`, `UpdateMemoryImportance`)
   - Working memory tools namespaced to `subagent/{taskId}` (writes go here automatically)
   - Skill tools (`GetSkill`, `ListSkills`, `SaveSkill`)
   - Registry tools (MCP, scheduling, etc.) — subagent management tools excluded
   - `ReportProgress` (baked with `taskId` + `primarySessionId`)
5. Calls `AgentLoopRunner.RunAsync` — the same loop used by `UserMessageHandler`
6. On `OperationCanceledException`: re-throws (propagates to `SubagentManager`)
7. On other exceptions: captures as `isSuccess=false`, `error=ex.Message`
8. Publishes `SubagentResultMessage` to `subagent.result`

The subagent uses `AgentLoopRunner` directly — the same code path as the primary
agent — so it gets text-based tool call parsing, hallucination nudging, completion
evaluation with re-prompting, context overflow trimming, and large tool result
chunking for free.

---

## Primary-side handlers

Both handlers follow the same pattern:

1. Build a synthetic user turn from the message fields
2. Record it in `IConversationMemory` for the primary session
3. Call `AgentContextBuilder.BuildAsync(primarySessionId, syntheticTurn, ct)` to
   reconstruct the full primary-agent context (system prompt, rules, history,
   memories, skills, working memory)
4. Build the same tool set as `UserMessageHandler`
5. Call `AgentLoopRunner.RunAsync` to let the LLM react naturally
6. Record the assistant response in conversation memory
7. Publish `AgentReply` (IsFinal=true) to `UserProxyTopics.UserResponse`

`SubagentResultHandler` checks for working memory entries under `subagent/{taskId}/` and
includes a retrieval hint in the synthetic user turn if any exist. It does not delete them —
they expire naturally via their TTL.

**Synthetic user turns:**

| Handler | Turn format |
|---|---|
| `SubagentProgressHandler` | `[Subagent task {taskId} reports]: {message}` |
| `SubagentResultHandler` (success) | `[Subagent task {taskId} completed]: {output}` |
| `SubagentResultHandler` (failure) | `[Subagent task {taskId} completed with error: {error}]: {output}` |

This approach means the primary agent's response to progress/results is fully
LLM-driven — it can ask follow-up questions, update memory, run additional tools,
or simply relay the information naturally.

---

## Configuration

```csharp
public sealed class SubagentOptions
{
    public int MaxConcurrentSubagents { get; set; } = 3;
    public int DefaultTimeoutMinutes { get; set; } = 10;
    public int LineageInlineBudgetChars { get; set; } = 24_000; // prior work inlined per subagent
    public int ResearchIterationFloor { get; set; } = 20;       // min max_iterations for research tasks
    // (consolidation timeouts omitted)
}
```

Override in `appsettings.json`:

```json
{
  "Subagent": {
    "MaxConcurrentSubagents": 5,
    "DefaultTimeoutMinutes": 20
  }
}
```

Or at registration time:

```csharp
agent.AddSubagents(opts =>
{
    opts.MaxConcurrentSubagents = 5;
    opts.DefaultTimeoutMinutes = 20;
});
```

---

## DI registration

```csharp
agent.AddSubagents();
```

Registers:

| Service | Lifetime | Purpose |
|---|---|---|
| `ISubagentManager` / `SubagentManager` | Singleton | Task lifecycle + concurrency |
| `SubagentRunner` | Transient | Per-task LLM loop |
| `IMessageHandler<SubagentProgressMessage>` / `SubagentProgressHandler` | Scoped | Primary-side progress handler |
| `IMessageHandler<SubagentResultMessage>` / `SubagentResultHandler` | Scoped | Primary-side result handler |
| `SubagentToolRegistrar` | Hosted service | Registers spawn/cancel/list tools at startup |
| `IToolSkillProvider` / `SubagentToolSkillProvider` | Singleton | Tool guide for the LLM |

Also subscribes to topics `subagent.progress` and `subagent.result`.
