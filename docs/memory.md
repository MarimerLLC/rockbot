---
title: Memory
nav_order: 6
---

# Memory subsystem

RockBot uses a three-tier memory architecture. Each tier has a different scope, lifetime, and
injection strategy, designed so the agent always has the right information in context without
token bloat.

---

## Overview

| Tier | Class | Scope | Lifetime | Injection |
|---|---|---|---|---|
| **Long-term** | `FileMemoryStore` | Cross-session | Permanent | BM25 (+ vector) delta per turn |
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
    DateTimeOffset CreatedAt,                           // First-seen agent-time
    DateTimeOffset? UpdatedAt,                          // Last rewritten (edits, rephrasing)
    IReadOnlyDictionary<string, string>? Metadata,      // Arbitrary key-value data
    float ImportanceScore = 0.5f                        // Salience 0.0–1.0
)
{
    public DateTimeOffset LastSeenAt { get; init; } = CreatedAt;   // Last reinforcement (save-event merged in)
    public int ReinforcementCount { get; init; } = 1;              // Distinct observations consolidated
}
```

#### Temporal semantics

Three time fields on each entry capture how the agent's relationship with a fact has
evolved, and they are used by the consolidation pass and by search-result formatting:

| Field | Meaning | When it advances |
|---|---|---|
| `CreatedAt` | First-seen agent-time | Set on entry creation; carried forward to `min(sources)` on merge |
| `LastSeenAt` | Last reinforcement — last time a fresh save-event produced content that merged into this fact | Set to `max(sources.LastSeenAt)` on merge; bumped to `now` on episode reinforcement; **not** bumped on dream rephrasing, importance decay, or other record edits |
| `UpdatedAt` | Record last rewritten | Bumped on any edit, including pure rephrasing |
| `ReinforcementCount` | Count of distinct observations consolidated into this entry | Starts at 1; summed across sources on merge; incremented by 1 on episode reinforcement |

A 5×-reinforced-over-2-years entry is treated differently from a one-off — both by the
dream LLM (see the "Temporal merging rules" section in `dream.md`) and by search
result formatting (`seen 4× from 2024-06 to today` vs. `first seen today`).

`LastSeenAt` is also the key for two load-bearing behaviors:

- **Importance decay** (in `DreamService.RunImportanceDecayPassAsync`) fades stale entries
  based on how long since the last *reinforcement*, not how long since the last record
  edit. Dream housekeeping (rephrasing, recategorization, score adjustments) does not
  reset the decay clock — only real save-event merges do. Decay is **exponential with a
  tunable half-life** — see [Importance decay shape](#importance-decay-shape) below.
- **No-query search ranking** (in `FileMemoryStore.SearchAsync` when no query text is
  supplied) orders results by `LastSeenAt` descending, surfacing recently-reinforced
  facts ahead of entries that dream has merely been polishing.

#### Importance decay shape

Decay is designed for an agent running over months-to-years, not weeks:

| Phase | Duration | Behavior |
|---|---|---|
| **Grace** | `ImportanceDecayGraceDays` (default 30) | Entry's score is untouched. |
| **Decay** | After grace | Score multiplied by `0.5^(elapsedDays / HalfLifeDays)` based on calendar time since last touched. Drops quickly at high scores; slows as it approaches the floor. |
| **Floor** | Once score reaches `ImportanceDecayFloor` (default 0.10) | No further decay. Entry remains discoverable via keyword match. |

**Decay is calendar-time based, not cycle-based.** Each decay pass computes the actual
elapsed time (in calendar days) since the entry was last touched and applies the
corresponding exponential factor. Because multiplicative decay composes — `0.5^(a/T) ·
0.5^(b/T) == 0.5^((a+b)/T)` — running the dream cycle twice a day, once a day, or once
a week all produce the same calendar-time decay curve for a given half-life. No tuning
needed when you change `CronSchedule`.

With the defaults (Grace=30, HalfLife=45, Floor=0.10), time from last reinforcement to
floor by starting importance:

| Starting importance | Time to floor (approx) |
|---|---|
| 0.95 (core fact) | ~176 days (~6 months) |
| 0.70 (significant) | ~156 days (~5 months) |
| 0.50 (routine) | ~134 days (~4.5 months) |
| 0.30 (minor) | ~101 days (~3.5 months) |

All three parameters are configurable on `DreamOptions`:
`ImportanceDecayGraceDays`, `ImportanceDecayHalfLifeDays`, `ImportanceDecayFloor`.
Set `ImportanceDecayHalfLifeDays <= 0` to disable decay entirely.

Legacy JSON files predating these fields deserialize with sensible defaults —
`LastSeenAt = CreatedAt`, `ReinforcementCount = 1` — via init-only property defaults.
No migration is required.

#### Subject-time metadata convention

A memory has two independent time axes: **agent-time** (when the agent learned the
fact, tracked by `CreatedAt`/`LastSeenAt`) and **subject-time** (when the thing the
fact is about actually happened — user's childhood, a trip in 2019, a decade lived
in a city). Subject-time is captured lazily via optional well-known keys on `Metadata`,
populated by the extraction LLM only when confident:

| Key | Contents |
|---|---|
| `subjectTime` | ISO 8601 point-in-time reference (`"2019-06-14"`, `"2019-06"`, `"2019"`). Use the most specific form the source justifies. |
| `subjectTimeStart` / `subjectTimeEnd` | Range bounds. Either may be omitted if open. |

These are a convention, not typed fields. They ride on the existing `Metadata`
dictionary and require no schema change. The extraction prompt (in `memory-rules.md`)
instructs the LLM to populate them only when confident and omit them for fuzzy
references or durable facts with no meaningful "when." The consolidation merge
preserves them across dedupe (start/end widen, point prefers the most-specific
value) via `DreamService.MergeSubjectTimeMetadata`.

### Storage

`FileMemoryStore` persists entries as JSON files organized by category:

```
{agentDataPath}/memory/{category}/{id}.json
```

The store maintains a lazy-loaded in-memory index protected by a `SemaphoreSlim`.

### Search and recall

#### BM25 keyword search

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

#### Hybrid vector search (optional)

When a text embedding model is configured (`EmbeddingOptions`), all three stores — long-term
memory, skills, and working memory — use **hybrid ranking** that combines BM25 keyword scores
with cosine similarity from vector embeddings. This improves recall for semantically similar
content that may not share the same keywords.

**How it works:**

1. `EmbeddingCache` generates embeddings on demand via any OpenAI-compatible endpoint and
   caches them as binary float arrays in `{basePath}/.embeddings/{id}.bin`.
2. `HybridRanker` normalizes both BM25 and cosine similarity scores to [0, 1] and averages
   them for a consolidated ranking.
3. Vector results below `MinSimilarityThreshold` (default 0.5) are excluded to prevent loosely
   related content from diluting keyword matches.

**Configuration:**

```csharp
public sealed class EmbeddingOptions
{
    public string? Endpoint { get; set; }               // e.g. "http://ollama:11434"
    public string? Model { get; set; }                  // e.g. "nomic-embed-text"
    public string? ApiKey { get; set; }                 // optional for Ollama
    public int MaxInputChars { get; set; } = 30_000;    // truncation limit (~7500 tokens)
    public float MinSimilarityThreshold { get; set; } = 0.5f;
}
```

Set via environment variables or `appsettings.json`:

```
Embedding__Endpoint=http://ollama:11434
Embedding__Model=nomic-embed-text
Embedding__ApiKey=              # optional for Ollama
```

When not configured, all stores fall back to BM25-only search with no loss of functionality.
Embeddings are generated asynchronously in the background and never block the agent's response.

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
| `search_memory(query?, category?, mode?)` | Hybrid/regex search with an optional category prefix filter; omit `query` to browse and get the category taxonomy |
| `delete_memory(id)` | Remove a specific entry |
| `update_memory_importance(id, importance)` | Re-score an existing entry |

There is no separate list-categories tool. Calling `search_memory` without a `query`
returns the most recently reinforced entries in scope followed by the full category
taxonomy, so `search_memory()` alone answers "how is knowledge organized here?". The
taxonomy is appended even when the search matched nothing, so a `category` filter that
misses still shows what categories do exist.

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
parameter on `search_working_memory`.

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
| `search_working_memory(query?, category?, tags?, namespace?)` | Search or list, optionally cross-namespace |

There is no separate `list_working_memory` tool — omitting `query` *is* the listing.
`search_working_memory` has two rendering modes:

| `query` | Ranking | Output |
|---|---|---|
| supplied | BM25 (+ vector when embeddings are configured), capped at `MaxResults` (20) | key, category, tags, expiry, and a 120-char content preview |
| omitted | most-recently-stored first, capped at 500 | key, category, tags, expiry — no preview |

The higher cap on the listing path preserves the unbounded enumeration the removed
`list_working_memory` tool provided; a listing that hits the cap says so.

**Cross-namespace access examples:**

```
# See what a completed subagent stored
search_working_memory(namespace: "subagent/t1b2c3")
get_from_working_memory("subagent/t1b2c3/research_results")

# Browse all patrol outputs
search_working_memory(namespace: "patrol")
search_working_memory(query: "alert", namespace: "patrol/heartbeat")

### The recall-search family

Two adjacent searches. They are discriminated by **what the caller is after**, not by which
subsystem stored it — a model choosing between them knows what it wants to find and has no way
to know which store holds it:

| Tool | Headline | Scope |
|---|---|---|
| `search_memory` | RECALL WHAT YOU CONCLUDED | Durable cross-session knowledge the agent chose to keep |
| `search_working_memory` | RECALL WHAT A TOOL RETURNED | This session's cached payloads |

Two rules hold the family together, both enforced by `RecallToolFamilyTests`:

1. **Every description leads with its headline and names its siblings.** The headline is the
   only part a model is guaranteed to read when scanning similar tools.
2. **Every empty result names the siblings.** A query that matches nothing is where a
   mis-routed lookup either recovers or hardens into "I was never told this" — so an empty
   result says so explicitly ("not evidence it was never said or never known") and points
   elsewhere. It never re-suggests the tool that just came back empty, which would be a retry
   loop rather than a recovery.

   Query-less *browses* are exempt. `search_memory()` with no query means "how is knowledge
   organised here?" and answers itself with the category taxonomy; an empty namespace listing
   in working memory is a fact about that namespace, not a failed lookup.

The tool names, headlines, and scope phrases are `const`s on `RecallTools` in
`RockBot.Host.Abstractions`, an assembly every registrar can see. Nothing else prevents a
rename or a re-wording from silently desynchronising a family whose members are registered
from assemblies that do not reference each other.

A third member — `search_conversation_history`, for turns that have scrolled out of the
context window — is tracked by [#509](https://github.com/MarimerLLC/rockbot/issues/509) and
blocked on [#530](https://github.com/MarimerLLC/rockbot/issues/530).

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

Reviews a gated subset of long-term memory entries for duplicates, near-duplicates, and
outdated content.

**Inputs provided to the LLM:**
- Only *eligible* entries — those new or changed since their last review, plus those in a
  near-duplicate cluster — numbered with ID, category, tags, content, and temporal context
  (`first=`, `last=`, `reinforced=N×`, and `subject=...` when subject-time metadata is
  present). Everything else is withheld and cannot be touched this cycle.
- Recent feedback signals (last 7 days, up to 50) for quality context

**What the LLM can do:**
1. Merge duplicate or near-duplicate entries — even when widely separated in time, treating
   them as reinforcement rather than novelty
2. Refine categories and tags
3. Flag noisy or redundant entries for archiving
4. Write `anti-patterns/{domain}` entries from Correction feedback

**Why gating exists:** without it, the whole corpus is re-offered for deletion on every cycle,
so per-entry survival compounds against you — at twice a day, a one-in-a-thousand misjudgement
per entry per cycle loses about half the corpus in a year. See `dream-service.md` for the
eligibility rules and tuning knobs.

**Temporal arithmetic on merge (computed by the host, not the LLM):**

- `CreatedAt` = `min(sources.CreatedAt)` — earliest first-seen preserved
- `LastSeenAt` = `max(sources.LastSeenAt)` — most recent reinforcement preserved; never
  reset to `now` on dream housekeeping
- `ReinforcementCount` = `sum(sources.ReinforcementCount)` — observations accumulate
- `ImportanceScore` = LLM-provided, else `max(sources.ImportanceScore)`
- `Metadata` = subject-time keys merged via `MergeSubjectTimeMetadata` (start/end widen,
  point prefers most-specific); other metadata keys are dropped (entry-scoped, ambiguous
  across merges)

This split keeps the LLM focused on *what to merge* while the host guarantees consistent
temporal arithmetic and prevents the dream cycle from inadvertently stamping every
reprocessed entry as "just seen today."

**Two safeguards constrain what a pass may do:**

- *Merge coverage* — every proper noun, acronym and multi-digit number in a merge's sources
  must survive into the merged text, or the merge is rejected and the sources are kept.
- *High-value floor* — entries at or above `Dream:PruningProtectionImportance` (0.80) or
  `Dream:PruningProtectionReinforcementCount` (5) may be merged but never pruned outright.

Both are deterministic rather than prompt guidance, because the prompt-level versions were
already present and did not hold. Merged entries record `mergedFrom` / `mergedAt` in metadata
(metadata is not part of the search surface). See `dream-service.md` for measured behaviour.

**Removals are archived, not deleted.** Consolidation calls `ArchiveAsync`, which hides an
entry from search but keeps it on disk and retrievable by ID; a separate purge pass
hard-deletes archived entries after `Dream:MemoryArchiveRetention` (default 90 days), and
`IArchivedMemoryMaintenance.RestoreAsync` brings one back. Merged entries are saved before
their sources are archived, and only sources whose replacement actually persisted are retired.

**Exhaustive-removal contract:** The union of explicit `toDelete` IDs and all `sourceIds` from
persisted merged entries is archived — preventing orphaned source entries even if the LLM
omits some IDs. IDs outside the eligible set are ignored.

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
7. [User sessions only] Inject subagent research outlines: workingMemory.ListAsync("subagent") —
   filters to `-index` keys only, showing the agent what prior subagent research is available
   in working memory without listing every content chunk. The agent can retrieve an index to
   see the document outline and navigate to specific chunks.
8. Replay last 20 conversation turns
```

For patrol sessions (`workingMemoryNamespace = "patrol/{taskName}"`), steps 6–7 are skipped
and step 5 uses the patrol namespace instead of `session/{sessionId}`.

---

## See also

- [knowledge-graph.md](knowledge-graph.md) — Entity-relationship graph for structured
  relational reasoning (entities, triples, BFS traversal, dream extraction/consolidation)
