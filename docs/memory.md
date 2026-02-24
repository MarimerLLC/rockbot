# Memory subsystem

RockBot uses a three-tier memory architecture. Each tier has a different scope, lifetime, and
injection strategy, designed so the agent always has the right information in context without
token bloat.

---

## Overview

| Tier | Class | Scope | Lifetime | Injection |
|---|---|---|---|---|
| **Long-term** | `FileMemoryStore` | Cross-session | Permanent | BM25 delta per turn |
| **Working** | `HybridCacheWorkingMemory` | Global, path-namespaced | TTL (default 5 min) | Own-namespace inventory per turn |
| **Conversation** | `InMemoryConversationMemory` | Session | Process lifetime | Last N turns (default 20) |

---

## Long-term memory

### Data model

```csharp
public sealed record MemoryEntry(
    string Id,                                          // 12-char GUID fragment
    string Content,                                     // The fact or preference
    string? Category,                                   // e.g. "user-preferences/timezone"
    IReadOnlyList<string> Tags,                         // Searchable labels
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyDictionary<string, string>? Metadata       // Arbitrary key-value data
);
```

### Storage

`FileMemoryStore` persists entries as JSON files organized by category:

```
{agentDataPath}/memory/{category}/{id}.json
```

The store maintains a lazy-loaded in-memory index protected by a `SemaphoreSlim`.

### BM25 recall and delta injection

Every user message triggers a BM25 search against all memory entries. Only entries not yet
injected this session are added to context (delta injection via `InjectedMemoryTracker`):

```
Recalled from long-term memory (relevant to this message):
- [abc123def456] (user-preferences/timezone): User is in Chicago (America/Chicago, UTC-6)
- [xyz789abc012] (anti-patterns/file-operations): Don't use search_files for content search — use grep; BM25 recall is faster and more accurate
```

**Document text for BM25:** `content + space-separated tags + category` (with `/` and `-`
replaced by spaces).

**Fallback on first turn:** If BM25 returns no results on the opening turn, up to 5 entries are
injected without a query — ensuring the agent always has some memory context at session start.

### Categories

Categories are hierarchical, slash-separated strings. Conventional categories:

| Category prefix | Contents |
|---|---|
| `user-preferences/` | Durable user preferences (timezone, communication style, tool preferences) |
| `user-preferences/inferred` | Preferences inferred by the dream pass from conversation patterns |
| `anti-patterns/` | Things the agent should not do in a given domain (see below) |
| `project-context/` | Long-lived facts about specific projects or systems |
| `general` | Uncategorized entries |

### Anti-patterns

A specialized long-term memory category for negative learning. Anti-pattern entries describe
approaches the agent took that produced wrong or unhelpful results, and what to do instead.

**Format:**
```
Category: anti-patterns/{domain}
Content: "Don't [do X] for [reason Y] — instead [do Z]"
Tags: ["anti-pattern"]
```

**Examples:**
- `anti-patterns/file-operations`: "Don't use `search_files` for content search — use `grep`; BM25 recall returns better results"
- `anti-patterns/email`: "Don't send emails without confirming recipient — user may have multiple addresses for different contexts"

Anti-pattern entries are created by the dream memory pass when it detects Correction feedback
signals that indicate a clear failure pattern. They surface via BM25 alongside regular memories,
acting as actionable constraints during inference.

### Memory tools

| Tool | Purpose |
|---|---|
| `save_memory(content, category?, tags?)` | Queue a memory for background enrichment and save |
| `search_memory(query, category?, tags?)` | BM25 search with optional filters |
| `delete_memory(id)` | Remove a specific entry |
| `list_memory_categories()` | Browse the category hierarchy |

When `save_memory` is called, a background task calls the LLM to expand the raw content into
focused, well-formed memory entries (the expansion prompt is in `memory-rules.md`). The
original content is saved immediately; expansion never blocks the response.

---

## Working memory

### Purpose

Global, path-namespaced scratch space with TTL-based expiration. Shared across all execution
contexts — user sessions, patrol tasks, and subagents. Typical uses:

- Large tool results that are too big to keep in conversation context (e.g. chunked web pages, oversized MCP responses)
- Partial results being assembled across tool calls
- Data handoff between subagents/patrol tasks and the primary agent
- Temporary state needed across multiple turns but not worth persisting long-term

### Namespace design

Keys are full path strings. The first two path segments form the **namespace** — which is
automatically baked into `WorkingMemoryTools` at construction time:

| Context | Namespace | Example key |
|---|---|---|
| User session | `session/{sessionId}` | `session/abc123/emails_inbox` |
| Patrol task | `patrol/{taskName}` | `patrol/heartbeat/latest_alert` |
| Subagent | `subagent/{taskId}` | `subagent/t1b2c3/research_results` |

Writes always go to the caller's own namespace. Reads can cross namespaces — either by passing
a full path key (e.g. `subagent/t1b2c3/research_results`) or by using the optional `namespace`
parameter on `list_working_memory` and `search_working_memory`.

### Data model

```csharp
public sealed record WorkingMemoryEntry(
    string Key,                      // Full path key (e.g. "session/abc123/emails_inbox")
    string Value,
    DateTimeOffset StoredAt,
    DateTimeOffset ExpiresAt,
    string? Category,
    IReadOnlyList<string>? Tags
);
```

### Storage

`HybridCacheWorkingMemory` uses `IMemoryCache` for TTL-based eviction plus a flat
`ConcurrentDictionary<string, EntryMeta>` side index for enumeration.

- **Default TTL:** 5 minutes (configurable per entry)
- **Per-namespace limit:** 50 entries per namespace (first two key segments); configurable via `MaxEntriesPerNamespace`
- **Prefix filtering:** `ListAsync(prefix)` and `SearchAsync(criteria, prefix)` filter by key prefix

`FileWorkingMemory` wraps the in-memory store with disk persistence. Entries are grouped by
top-level key segment into files (`session.json`, `patrol.json`, `subagent.json`) under
`{BasePath}/working-memory/`. Expired entries are discarded on load and swept hourly.

### Injection

At the start of every user session turn, `AgentContextBuilder` injects two working memory sections:

**Own namespace** — the session's scratch entries:
```
Working memory (scratch space — use search_working_memory or get_from_working_memory to retrieve):
- session/abc123/emails_inbox: expires in 4m32s, category: email, tags: inbox, unread
- session/abc123/draft_reply: expires in 2m01s
```

**Patrol findings** — what patrol tasks have stored (user sessions only):
```
Patrol findings in working memory (use get_from_working_memory with the full key to read):
- patrol/heartbeat/latest-briefing: expires in 4h12m, category: patrol-finding
- patrol/heartbeat/alerts: expires in 4h08m, tags: urgent
```

The actual content is not included — the agent loads entries on demand to avoid token bloat.

For patrol sessions (`workingMemoryNamespace = "patrol/{taskName}"`), the context builder
injects the patrol's own entries under the patrol namespace and skips the patrol findings injection.

### Working memory tools

| Tool | Purpose |
|---|---|
| `save_to_working_memory(key, data, ttl_minutes?, category?, tags?)` | Store an entry in own namespace |
| `get_from_working_memory(key)` | Retrieve by plain key (own namespace) or full path (cross-namespace) |
| `list_working_memory(namespace?)` | List keys in own namespace, or pass a prefix to browse another |
| `search_working_memory(query?, category?, tags?, namespace?)` | BM25 search, optionally cross-namespace |

**Cross-namespace access examples:**

```
# See what a completed subagent stored
list_working_memory(namespace: "subagent/t1b2c3")
get_from_working_memory("subagent/t1b2c3/research_results")

# Browse all patrol outputs
list_working_memory(namespace: "patrol")
search_working_memory(query: "alert", namespace: "patrol/heartbeat")
```

---

## Conversation memory

### Purpose

Maintains the turn-by-turn conversation history for LLM context. Ephemeral — does not persist
across sessions or restarts.

### Storage

`InMemoryConversationMemory` stores turns in a `ConcurrentDictionary<sessionId, List<Turn>>`.

**Turn:** `{ Role: "user" | "assistant", Content: string }`

### Injection

The last `MaxLlmContextTurns` (default 20) turns are replayed into each LLM request. Older
turns are dropped to keep context bounded.

### Conversation log

An optional `IConversationLog` (backed by `FileConversationLog`) records turns to a persistent
JSONL file for use by the dream cycle. The log is cleared after each dream pass to prevent
unbounded growth.

---

## Dream cycle — memory passes

The dream service runs two memory-related passes:

### Pass 1 — Memory consolidation

Reviews all long-term memory entries for duplicates, near-duplicates, and outdated content.

**Inputs provided to the LLM:**
- All memory entries (up to 1000), numbered with ID, category, tags, and content
- Recent feedback signals (last 7 days, up to 50) for quality context

**What the LLM can do:**
1. Merge duplicate or near-duplicate entries, carrying forward the earliest `CreatedAt`
2. Refine categories and tags
3. Delete noisy or redundant entries
4. Write `anti-patterns/{domain}` entries from Correction feedback

**Exhaustive deletion contract:** The union of explicit `toDelete` IDs and all `sourceIds` from
merged entries are deleted — preventing orphaned source entries even if the LLM omits some IDs.

### Pass 2 — Preference inference

Analyzes the full conversation log for durable user preference patterns. Applies
sentiment-based thresholds before writing a preference:

- Very irritated (repeated strong correction): 1 occurrence
- Mildly frustrated (gentle pushback): 2 occurrences
- Minor/casual suggestion: 3+ occurrences

Inferred preferences are saved with:
- `category: "user-preferences/inferred"` (default)
- `tags: ["inferred"]`
- `metadata: { "source": "inferred" }`

Preferences touching security, credentials, or financial decisions get
`metadata["requires_user_permission"] = "true"` so the agent always confirms before acting.

The conversation log is cleared after this pass regardless of LLM success or failure.

---

## Configuration

```csharp
// Long-term memory
public sealed class MemoryOptions
{
    public string BasePath { get; set; } = "memory";   // Relative to agent data path
}

// Working memory
public sealed class WorkingMemoryOptions
{
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxEntriesPerNamespace { get; set; } = 50;  // per first-two-segment namespace prefix
    public string BasePath { get; set; } = "working-memory";
}

// Dream passes
public sealed class DreamOptions
{
    public string DirectivePath { get; set; } = "dream.md";           // Memory consolidation prompt
    public string PreferenceDirectivePath { get; set; } = "pref-dream.md"; // Preference inference prompt
    public bool PreferenceInferenceEnabled { get; set; } = true;
}
```

Custom directive files override the built-in fallbacks. Place them on the agent data volume.

---

## DI registration

```csharp
builder
    .WithMemory()             // Conversation + long-term (FileMemoryStore) + working memory
    .WithConversationLog()    // Required for preference inference dream pass
    .WithFeedback()           // Required for anti-pattern mining in dream pass
    .WithDreaming()           // Enables memory consolidation and preference inference passes
```

---

## Memory injection flow (per turn)

```
1. BM25 search: longTermMemory.SearchAsync(Query: message.Content, MaxResults: 8)
2. Fallback (first turn only): SearchAsync(MaxResults: 5) if no BM25 results
3. Delta filter: only entries not yet injected this session (InjectedMemoryTracker)
4. Inject as system message: "Recalled from long-term memory..."
5. Inject working memory inventory for own namespace: workingMemory.ListAsync("session/{sessionId}")
6. [User sessions only] Inject patrol findings: workingMemory.ListAsync("patrol") — shows the
   agent what patrol tasks have stored since the last run, without fetching content
7. Replay last 20 conversation turns
```

For patrol sessions (`workingMemoryNamespace = "patrol/{taskName}"`), step 6 is skipped and
step 5 uses the patrol namespace instead of `session/{sessionId}`.
