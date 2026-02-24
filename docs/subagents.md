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
    timeout_minutes?  // Max runtime — default 10 minutes
)
→ "Subagent spawned with task_id: abc123def456"
```

If the concurrency limit is reached, returns an error string starting with `"Error:"`.

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
  list_working_memory(namespace: "subagent/abc123")
  get_from_working_memory("subagent/abc123/summary")
```

**Why working memory instead of long-term memory:**

- Subagent outputs are temporary — they don't need to survive beyond the current conversation
- TTL (default 4 hours for subagent outputs) handles cleanup automatically
- No explicit cleanup required in `SubagentResultHandler` — entries expire naturally
- Cross-namespace reads are first-class in the working memory API, no workarounds needed

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
}
```

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
   - Long-term memory tools (`SaveMemory`, `SearchMemory`, `DeleteMemory`, `ListCategories`)
   - Working memory tools namespaced to `subagent/{taskId}` (writes go here automatically)
   - Skill tools (`GetSkill`, `ListSkills`, `SaveSkill`)
   - Registry tools (MCP, REST, scheduling, etc.) — subagent management tools excluded
   - `ReportProgress` (baked with `taskId` + `primarySessionId`)
5. Calls `AgentLoopRunner.RunAsync` — the same loop used by `UserMessageHandler`
6. On `OperationCanceledException`: re-throws (propagates to `SubagentManager`)
7. On other exceptions: captures as `isSuccess=false`, `error=ex.Message`
8. Publishes `SubagentResultMessage` to `subagent.result`

The subagent uses `AgentLoopRunner` directly — the same code path as the primary
agent — so it gets text-based tool call parsing, hallucination nudging, context
overflow trimming, and large tool result chunking for free.

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
