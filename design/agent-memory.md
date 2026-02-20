# Agent Memory System

## Overview

RockBot agents support two tiers of memory:

1. **Conversation memory** (ephemeral) — in-memory sliding window of recent turns per session, giving the LLM multi-turn conversation context. Lost on restart.
2. **Long-term memory** (persistent) — file-based knowledge store on a persistent volume, surviving restarts and redeployments. Managed autonomously by the LLM via tool calls.

## Architecture

### Tier 1: Conversation Memory

- `InMemoryConversationMemory` backed by `ConcurrentDictionary<sessionId, List<ConversationTurn>>`
- Sliding window: oldest turns evicted when `MaxTurnsPerSession` exceeded (default: 50)
- Idle sessions cleaned up after `SessionIdleTimeout` (default: 1 hour) via timer
- Per-session object lock for thread safety
- Returns snapshot copies from `GetTurnsAsync` to prevent mutation

### Tier 2: Long-Term Memory

- `FileMemoryStore` persists `MemoryEntry` records as JSON files
- Category system maps to subdirectories: `{memoryDir}/{category}/{id}.json`
- Uncategorized entries stored at `{memoryDir}/{id}.json`
- In-memory index loaded lazily on first access, walks all subdirectories
- Search: case-insensitive substring on content + tag filtering + category prefix match + date range
- Thread safety via `SemaphoreSlim(1,1)` for all file I/O
- Category paths sanitized: alphanumeric, hyphens, underscores, `/` only; no `..` or absolute paths

### LLM Tool Integration

The LLM manages its own long-term memory via three tools:

- **`save_memory(content, category?, tags?)`** — saves a `MemoryEntry` with LLM-chosen category
- **`search_memory(query?, category?)`** — keyword search with optional category prefix filter
- **`list_categories()`** — returns the category tree for self-organization

`FunctionInvokingChatClient` from `Microsoft.Extensions.AI` handles the tool-call loop automatically.

## Path Resolution

Memory directory follows the same pattern as `FileAgentProfileProvider.ResolvePath`:

```
MemoryOptions.BasePath absolute?       → use directly
AgentProfileOptions.BasePath absolute?  → combine(profileBase, memoryBase)
else                                    → combine(AppContext.BaseDirectory, profileBase, memoryBase)
```

Default: `{AppContext.BaseDirectory}/agent/memory/`
K8s PV: `agent.WithProfile(o => o.BasePath = "/data/agent")` → `/data/agent/memory/`

## Disk Layout

```
agent/                              # AgentProfileOptions.BasePath
├── soul.md
├── directives.md
├── style.md                        # optional
└── memory/                         # MemoryOptions.BasePath (writable)
    ├── a1b2c3d4.json               # uncategorized memory
    ├── user-preferences/           # LLM-created category
    │   └── e5f6g7h8.json
    └── project-context/            # hierarchical categories
        └── rockbot/
            └── i9j0k1l2.json
```

## Configuration

```csharp
builder.Services.AddRockBotHost(agent =>
{
    agent.WithMemory();                                         // both tiers, defaults
    // or independently:
    agent.WithConversationMemory(o => o.MaxTurnsPerSession = 100);
    agent.WithLongTermMemory(o => o.BasePath = "/data/agent/memory");
});
```

## Handler Flow

1. Build system prompt from profile (existing)
2. Record incoming user turn in conversation memory
3. Build chat messages: `[System(prompt), ...history turns]`
4. Call LLM with memory tools in `ChatOptions.Tools`
5. `FunctionInvokingChatClient` handles tool-call loop automatically
6. Record assistant turn in conversation memory
7. Publish final reply (existing)

## Error Handling

- Memory directory missing on first save → auto-created via `Directory.CreateDirectory`
- Memory file not found on `GetAsync` → return null
- Memory file not found on `DeleteAsync` → no-op
- Malformed JSON in memory file → log warning, skip entry in search results
- Conversation session not found → return empty list
- Invalid category path → `ArgumentException`

## Future Considerations

- Swap `FileMemoryStore` for vector/embedding store (interface is async and query-based)
- Add memory compaction/summarization for entries exceeding size thresholds
- Add memory import/export for agent migration
