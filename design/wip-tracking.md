# Work-In-Progress (WIP) Tracking

## Problem

When a message is pulled from RabbitMQ and processing begins, the message is acknowledged (removed from the queue) before the work completes. For `UserMessageHandler`, the handler returns immediately and spawns a fire-and-forget background LLM loop. If the pod crashes during that background loop, the message is gone from RabbitMQ and the in-progress work exists only in memory — the request is silently lost.

## Solution

Persist the message envelope to disk before handler dispatch begins. Clean it up when processing completes. On restart, recover incomplete entries by replaying them through the pipeline.

## Design Decisions

### Why a Dedicated WIP Store?

We considered three options:

1. **Working memory (`IWorkingMemory`)**: Already file-persisted with TTL, but it's designed as LLM scratch space. Mixing infrastructure state with agent reasoning state pollutes the tool surface and creates confusion about ownership.
2. **Long-term memory (`ILongTermMemory`)**: Designed for knowledge persistence, not transient processing state. Wrong abstraction level.
3. **Dedicated WIP store**: Clean separation of concerns. WIP is infrastructure plumbing — the LLM never sees it.

Option 3 was chosen. The WIP store is invisible to the LLM and has its own lifecycle (begin → complete/abandon), independent of memory TTL.

### Why File-Per-Entry?

Each in-flight message gets its own JSON file (`{basePath}/wip/{messageId}.json`). This provides:

- **Atomic operations**: File create/delete is atomic on POSIX and effectively atomic on Windows for our use case.
- **No contention**: Entries don't share a file, so concurrent messages don't compete for locks on the same resource.
- **Simple recovery**: `Directory.EnumerateFiles("*.json")` lists all incomplete work.
- **Debuggability**: Inspect individual files during incidents.

### Why Not Delayed Ack?

An alternative approach: don't ack the RabbitMQ message until the background loop finishes. On crash, the message is automatically requeued. This was rejected because:

- It blocks the consumer for the entire LLM loop duration (potentially minutes).
- It conflicts with the "ack early, process in background" pattern that prevents subagent re-spawn on pod restart (issue #122).
- Prefetch count would need to be 1, killing throughput.

### Why Middleware?

`WipMiddleware` sits in the message pipeline between `TracingMiddleware` and `ErrorHandlingMiddleware`. This ensures:

- **Every message type** is tracked without handler-specific code (for synchronous handlers).
- The WIP entry is created under the tracing span, so activity context is available in logs.
- If `ErrorHandlingMiddleware` catches an exception, the WIP entry persists (it was created before the error occurred).

Handlers that spawn background work opt into deferred completion via `context.Items[WipConstants.DeferredKey]`.

## Architecture

### Components

```
Message arrives from RabbitMQ
        │
        ▼
┌─────────────────┐
│ TracingMiddleware │  ← outer: starts activity span
└────────┬────────┘
         ▼
┌─────────────────┐
│  WipMiddleware   │  ← persists envelope to wip/{messageId}.json
└────────┬────────┘
         ▼
┌─────────────────┐
│ ErrorHandling MW │  ← catches exceptions, sets Retry/DeadLetter
└────────┬────────┘
         ▼
┌─────────────────┐
│  LoggingMiddleware │
└────────┬────────┘
         ▼
┌─────────────────┐
│ Terminal Handler │  ← dispatches to typed handler (UserMessageHandler, etc.)
└─────────────────┘
```

### Lifecycle

**Synchronous handlers** (ScheduledTaskHandler, A2A handlers):
1. `WipMiddleware.BeginAsync()` — writes `wip/{messageId}.json`
2. Handler runs to completion
3. `WipMiddleware` auto-completes — deletes the file

**Background handlers** (UserMessageHandler):
1. `WipMiddleware.BeginAsync()` — writes `wip/{messageId}.json`
2. Handler sets `context.Items["wip:deferred"] = true`
3. Handler launches `NativeLlmLoopAsync` or `BackgroundToolLoopAsync`, passing `wipMessageId`
4. Handler returns → `WipMiddleware` sees deferred flag, skips auto-complete
5. Background loop finishes → calls `wipTracker.CompleteAsync(messageId)` in `finally`

**Startup recovery** (AgentHost.StartAsync):
1. Scan `wip/` directory for `*.json` files
2. For each entry older than `StaleThreshold` (default 30 min): delete and log warning
3. For each non-stale entry: complete the old entry, re-dispatch through the pipeline with a `wip:recovery` header so handlers can detect replay

### Disk Layout

```
agent/                         # AgentProfileOptions.BasePath
├── memory/                    # long-term memory
├── working-memory/            # working memory
└── wip/                       # WIP entries (transient)
    ├── a1b2c3d4e5f6.json      # in-flight message
    └── f7e8d9c0b1a2.json      # in-flight message
```

Each file contains a JSON object:

```json
{
  "messageId": "a1b2c3d4e5f6",
  "messageType": "RockBot.UserProxy.UserMessage",
  "correlationId": "corr-abc",
  "replyTo": "user.response",
  "source": "user-proxy",
  "destination": null,
  "messageTimestamp": "2026-03-28T14:00:00Z",
  "startedAt": "2026-03-28T14:00:01Z",
  "headers": { "rb-source": "user-proxy" },
  "bodyBase64": "eyJ1c2VySWQiOi..."
}
```

## Configuration

```csharp
// Defaults — no configuration needed
builder.Services.Configure<WipOptions>(_ => { });

// Custom stale threshold
builder.Services.Configure<WipOptions>(o => o.StaleThreshold = TimeSpan.FromHours(1));
```

Via `appsettings.json`:

```json
{
  "Wip": {
    "BasePath": "wip",
    "StaleThreshold": "00:30:00"
  }
}
```

## Telemetry

Four counters in `HostDiagnostics` (zero-cost when no listener is attached):

| Metric | Description |
|--------|-------------|
| `rockbot.wip.begun` | WIP entries created (message received) |
| `rockbot.wip.completed` | WIP entries completed (processing finished) |
| `rockbot.wip.recovered` | WIP entries replayed on startup |
| `rockbot.wip.abandoned` | WIP entries too old to recover |

## Timing Gap

There is still a brief window where a crash can lose a request: if the pod recycles between pulling the message from RabbitMQ and `WipMiddleware` finishing the file write. This window is typically < 10ms. Eliminating it entirely would require a distributed transaction coordinator, which is out of scope.

## Error Handling

- **File write fails**: Exception propagates, `ErrorHandlingMiddleware` catches it, message is nacked (requeued by RabbitMQ). No data loss.
- **File delete fails on complete**: Logged as warning. Entry becomes a false positive on next startup, which is harmless — the replay will process the message again.
- **Malformed JSON on recovery**: Logged as warning, file skipped.
- **Recovery dispatch fails**: Logged as error, continue with next entry.
- **Idempotent completion**: `CompleteAsync` is a no-op if the file is already deleted.
