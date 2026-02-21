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
│     │  Tools: working memory (scoped) + registry +      │
│     │         ReportProgress + whiteboard               │
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

### Whiteboard tools

```
WhiteboardWrite(key, value)   // Write a value to the shared board
WhiteboardRead(key)           // Read a value by key (null if missing)
WhiteboardList()              // List all keys + value previews
WhiteboardDelete(key)         // Remove a key
```

Each subagent's whiteboard is namespaced by its `taskId` (used as the `boardId`).
The primary agent can read the same board using the same `taskId`.

---

## Whiteboard (`IWhiteboardMemory`)

The whiteboard is a concurrent-safe, cross-session key-value store used to pass
structured data between the primary agent and subagents.

```csharp
public interface IWhiteboardMemory
{
    Task WriteAsync(string boardId, string key, string value, CancellationToken ct = default);
    Task<string?> ReadAsync(string boardId, string key, CancellationToken ct = default);
    Task DeleteAsync(string boardId, string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> ListAsync(string boardId, CancellationToken ct = default);
    Task ClearBoardAsync(string boardId, CancellationToken ct = default);
}
```

The default implementation (`InMemoryWhiteboardMemory`) uses a
`ConcurrentDictionary<boardId, ConcurrentDictionary<key, value>>`. It is
ephemeral — boards are lost on pod restart. A file-backed implementation is
deferred to a future iteration.

**Usage pattern:**

```
Primary agent:
  WhiteboardWrite("abc123", "urls-to-scrape", "[url1, url2, url3]")
  spawn_subagent("Scrape each URL in whiteboard key 'urls-to-scrape' and write findings to 'scraped-results'")

Subagent:
  WhiteboardRead("urls-to-scrape") → "[url1, url2, url3]"
  [processes each URL]
  WhiteboardWrite("scraped-results", "summary", "...")
  ReportProgress("Done scraping. Results written to whiteboard.")

Primary agent (on result):
  WhiteboardRead("scraped-results") → "..."
```

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
   - Working memory tools scoped to `subagentSessionId`
   - Registry tools (MCP, REST, scheduling, etc.) via `SubagentRegistryToolFunction`
   - `ReportProgress` (baked with `taskId` + `primarySessionId`)
   - Whiteboard tools (baked with `taskId` as board ID)
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
| `IWhiteboardMemory` / `InMemoryWhiteboardMemory` | Singleton | Cross-session data handoff |
| `SubagentRunner` | Transient | Per-task LLM loop |
| `IMessageHandler<SubagentProgressMessage>` / `SubagentProgressHandler` | Scoped | Primary-side progress handler |
| `IMessageHandler<SubagentResultMessage>` / `SubagentResultHandler` | Scoped | Primary-side result handler |
| `SubagentToolRegistrar` | Hosted service | Registers spawn/cancel/list tools at startup |
| `IToolSkillProvider` / `SubagentToolSkillProvider` | Singleton | Tool guide for the LLM |

Also subscribes to topics `subagent.progress` and `subagent.result`.
