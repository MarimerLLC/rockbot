# Large Result Handling (Chunking → Blob + Range)

## Why

Tool results, MCP responses, A2A task results, and web fetches routinely arrive larger
than we want to drop into the LLM context. Today we **chunk on entry**: the moment a large
result lands in memory, `ChunkingAIFunction` splits it at Markdown heading / blank-line
boundaries, writes each chunk as its own working-memory entry, and hands the model an index
table to navigate by section. The model then pulls chunks by key with
`get_from_working_memory`.

That design works, but it has three structural problems and one memory problem:

1. **Eager work that's often wasted.** Every large result is split, N entries are written,
   and an outline is built *up front* — even when the model needs one section or none.
2. **Namespace-cap pressure.** Working memory caps each namespace at
   `MaxEntriesPerNamespace` (default **50**, `WorkingMemoryOptions.cs`). A single large
   result can consume many slots as `chunkN` keys, and a couple of big results can evict
   each other. Over-cap writes are **silently dropped** (`HybridCacheWorkingMemory.SetAsync`).
3. **No benefit for non-Markdown payloads.** `ContentChunker` is heading-aware. JSON, CSV,
   and logs — the bulk of MCP/A2A payloads — have no headings, so `SplitAtHeadings` yields
   one giant section that falls through to blank-line splitting and finally a **hard split at
   20k chars**. The "outline" degrades to `Part 0, Part 1, …` with zero semantic value. We
   paid the full eager-chunking cost and the model is left fetching arbitrary 20k windows
   anyway — which is the range model, just clumsier and without offset control.
4. **It all lives in RAM.** The production `IWorkingMemory` is `FileWorkingMemory`, which
   wraps `HybridCacheWorkingMemory` (`IMemoryCache`) and keeps **every value in process
   memory at all times** — the PVC under `/data/agent/working-memory/` is a
   checkpoint/restore only, with no read-through. So a 64k (or 6 MB) blob sits in pod RAM
   for its entire 20-minute TTL. A large tool result is the worst possible thing to park in
   RAM: big, read non-randomly, and read maybe once.

## What we already do (so we're precise about the delta)

The model **already** "asks for content on demand" — it just asks by **pre-decided semantic
key**, not by **range**, and the bytes already sit in RAM rather than on disk. The full blob
never enters chat history today; only the index does. A2A does the same without even chunking
(`A2ATaskResultHandler` stores the whole result and hands the model a key).

So the real question is narrow:

> At ingestion, do we (A) eagerly split into N semantic chunk-keys + outline in RAM
> [today], or (B) store one blob as a TTL-swept **file on a PVC**, put only a small
> pointer + lazily-built outline in working memory, and serve **ranges and sections**
> off disk on demand?

This doc proposes (B).

## Proposal

Store the full result **once**, as a file on a PVC, and reduce the working-memory footprint
to a small pointer. Serve windows off disk on demand — a file is inherently
range-addressable (`seek + read`), so a range read costs only the bytes requested, not the
whole blob.

```
Large result (tool / MCP / A2A / web)
    │
    ▼
LargeResultGateway  (single choke point, see "Where it hooks")
    │   size < inline threshold → return inline (unchanged)
    │   size ≥ threshold        → write blob to ${ROCKBOT_SHARED_PATH}/results/{handle}
    │                             sniff content-type (markdown / json / text)
    │                             working-memory entry ← { handle, size, contentType }
    │                                                    (small; one cap slot; BM25-searchable)
    ▼
Tool result entering chat history:
    "Result is 64,210 chars (json), stored at handle r-9f3a… .
     Use read_range(handle, offset, length) to scan,
     or outline(handle) then read_section(handle, heading) for Markdown."
```

Retrieval is two modes against the **same single blob**:

- **`read_range(handle, offset, length)`** — windowed read for JSON / CSV / logs and
  "keep scanning" loops. Streams just that slice off disk; nothing else enters RAM.
- **`outline(handle)` + `read_section(handle, heading)`** — builds the Markdown outline
  **lazily on first request** (reusing `ContentChunker`) and serves one section. We only pay
  the chunking cost when the model actually wants semantic navigation, and only for content
  where headings exist.

Content-type sniffing at ingestion just *hints* which mode to prefer (markdown → "call
outline"; json/log → "use read_range").

### Why keep both modes

Pure ranges regress the Markdown/prose case, where today's outline nails a targeted fetch in
one hop — blind windowing makes the model guess offsets and scan. Pure eager chunking is the
status quo we're trying to fix. The model handles **both** kinds of payload, so it needs both
retrieval modes; the storage shape (one blob on disk) is what's common to them.

## Storage location

Blobs live under `${ROCKBOT_SHARED_PATH}/results/` (defaults to `/rockbot/shared/results/`
when unset) — the same **shared PVC** convention the attachment gateway already uses (see
[`mcp-attachments.md`](mcp-attachments.md)). This is deliberate:

- `/rockbot/shared` is **ReadWriteMany** (longhorn). Blobs are reachable across pods —
  subagents, A2A partners, and script pods can read a result the primary agent fetched.
- `/data/agent` is **ReadWriteOnce** and private to the agent pod; it can't support cross-pod
  handoff, so it's the wrong home for shareable results.
- Ephemeral pods (research agent, advisor council) already run with `Memory__BasePath=/tmp/memory`
  and no PVC. For those, the blob store should fall back to a **local temp dir** so the blob
  vanishes with the pod and incurs no PVC overhead.

This points at a thin `ILargeObjectStore` abstraction with a per-deployment backing
(local `/tmp` for ephemeral pods, shared RWX PVC for the durable agent) rather than a single
hard-coded path. It keeps the "process isolation / nothing trusts the LLM" model intact: a
result is just a file behind a handle — no shared process memory.

## Where it hooks

The transform belongs at the **single choke point each source already funnels through**, so
behavior is uniform regardless of entry path:

- **MCP** — `McpBridgeService.HandleToolInvokeAsync` (where attachment rewriting already
  lives).
- **Tool results generally** — the `ChunkingAIFunction` decorator seam, repurposed from
  "split into N keys" to "stash one blob + return pointer."
- **A2A** — `A2ATaskResultHandler`, which already stashes the whole result; swap its
  working-memory write for a blob write + pointer.
- **Web** — `WebBrowseToolExecutor`, same swap.

The per-call `ToolResultMaxChars` cap (`RockBotFunctionInvokingChatClient`) and the
watermark trim (`AgentLoopRunner.TrimLargeToolResultsAsync`) stay as the last-line defense
for anything that slips through inline; they should target the blob store for their stash so
all three mechanisms write to one place.

## Lifecycle / cleanup

Working memory gives TTL auto-expiry for free; files do not. Without cleanup we accumulate
orphaned blobs (exactly the open item the attachment gateway punted on —
"TTL cleanup is a follow-up").

- TTL-stamp each blob (mirror the **hourly sweep** `FileWorkingMemory` already runs over its
  group files) and delete expired blobs.
- Alternatively / additionally, tie blob lifetime to session end.
- Keep the pointer's working-memory TTL and the blob's TTL aligned so the model never holds a
  handle whose bytes are gone (and `read_range` on a missing handle returns a clear "expired"
  message, like `GetFromWorkingMemory` does today).

## What does NOT happen

- **No loss of semantic navigation.** The Markdown outline survives — it's just built lazily
  from the single blob instead of pre-materialized as N chunk copies.
- **No full-blob load on read.** `read_range` seeks; it does not read the file into RAM. Only
  `outline` reads the whole blob (to split it), and only when the model asks.
- **No BM25 over raw blobs.** Bytes stay on disk and out of the search index; the small
  outline/pointer stays in working memory and remains searchable. Large results shouldn't
  dominate the index anyway.
- **No new orchestration burden on the model.** It receives a handle and a one-line hint, the
  same ergonomics as today's index table — not a multi-call upload/download dance.

## Open questions / validate first

This proposal rests on one empirical claim worth confirming before refactoring load-bearing
code:

- **How often are large results non-Markdown, and how big do they get?** That tells us whether
  the range path or the outline path is the common case, and therefore which PVC access mode
  we actually need. Instrument `ChunkingAIFunction` to log content-type and size distribution
  for a week of real traffic.
- **Read latency on RWX longhorn.** Each `read_range` is a network hop on the shared PVC.
  It's agent-driven (one per tool call) and dwarfed by the LLM round-trip, but worth measuring
  before committing.
- **Subagent topology.** If subagents run in-process they share the agent's RAM and PVC; if
  they're separate pods they need the RWX volume for handoff. Confirms the volume choice.

## Thresholds (today, for reference)

| Mechanism | Threshold | Source |
|---|---|---|
| Tool-result chunking | 64,000 chars (~16k tokens) | `ModelBehavior.ToolResultChunkingThreshold` |
| Per-call hard cap | 8,000 chars | `AgentHostOptions.ToolResultMaxChars` |
| Watermark soft trim | 30,000 tokens | `AgentHostOptions.ToolResultStashWatermarkTokens` |
| Web page chunking | 8,000 chars | `WebToolOptions.ChunkingThreshold` |
| Chunk max length | max(threshold, 20,000) | `ChunkingAIFunction` |
| Per-namespace entry cap | 50 entries | `WorkingMemoryOptions.MaxEntriesPerNamespace` |

See [`mcp-attachments.md`](mcp-attachments.md) for the shared-PVC + handle pattern this builds
on, [`agent-memory.md`](agent-memory.md) for the memory tiers, and
[`mcp-bridge.md`](mcp-bridge.md) for the bridge choke point.
