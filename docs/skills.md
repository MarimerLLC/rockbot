# Skills subsystem

Skills are named markdown procedure documents that the agent accumulates over time and consults
to handle recurring tasks correctly and consistently. Unlike memories (which store facts and
preferences), skills store *how to do something* — step-by-step instructions, decision trees,
and tool-invocation patterns.

---

## Data model

```csharp
public sealed record Skill(
    string Name,        // Hierarchical identifier, e.g. "plan-meeting" or "mcp/email"
    string Summary,     // One-line description (≤15 words); shown in the skill index
    string Content,     // Full markdown procedure the agent reads and follows
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? LastUsedAt = null,
    IReadOnlyList<string>? SeeAlso = null   // Related skill names (see below)
);
```

### Naming conventions

Skill names use lowercase, hyphens, and optional `/` path separators:

- `plan-meeting` — simple single-purpose skill
- `mcp/email` — domain-grouped skill (`mcp` prefix)
- `research/summarize-paper` — nested grouping

The `/` separator enables **prefix cluster detection** during dream consolidation (see below).

---

## Storage

`FileSkillStore` (`src/RockBot.Host/FileSkillStore.cs`) persists each skill as a JSON file:

```
{agentDataPath}/skills/{name}.json
```

Path separators in the name become directory separators, so `mcp/email` is stored at
`skills/mcp/email.json`. The store maintains a lazy-loaded in-memory index for fast lookups,
protected by a `SemaphoreSlim` for thread safety.

---

## Discovery and injection

### Session-level index (once per session)

At the start of each session, all skills are listed and a compact index is injected into the
system prompt:

```
Available skills (use get_skill to load full instructions):
- mcp/email: Send email via an MCP-connected mail server
- plan-meeting: Schedule meetings and invite attendees
- research/summarize-paper: Summarize an academic paper into key points
```

This gives the agent a complete catalog. Full skill content is loaded on demand via `get_skill`.

### Per-turn BM25 recall

Every user message triggers a BM25 search against the skill index (name + summary). The top 5
matching skills not yet injected this session are surfaced as:

```
Relevant skills for this message: mcp/email, mcp/guide
```

This means skills bubble up as the conversation topic shifts — the agent doesn't need to know in
advance which skill it needs.

**Document text for BM25:** skill name (with `/` and `-` replaced by spaces) + summary.
So `mcp/email` ranked against the query `"send email attachment"` scores on tokens
`"mcp email send email via mcp connected mail server"`.

### See-also injection (serendipitous discovery)

When a skill is recalled via BM25, its `SeeAlso` references are immediately checked. Any
referenced skill not yet seen this session is injected as:

```
Related skills (see-also): mcp/guide, mcp/calendar
```

This enables serendipitous discovery — the agent learns about `mcp/guide` when it recalls
`mcp/email`, even if the query wouldn't have matched `mcp/guide` directly.

---

## Skill tools

The LLM can call these tools (registered via `SkillTools`):

| Tool | Purpose |
|---|---|
| `get_skill(name)` | Load full skill content; records usage for dream optimization |
| `save_skill(name, content)` | Create or update a skill |
| `list_skills()` | List all skills with summaries |
| `delete_skill(name)` | Remove a skill |

When `save_skill` is called, a background task immediately generates a one-sentence summary (≤15
words) for the skill index. The skill is persisted first; summary generation never blocks the
response.

---

## Usage tracking

`FileSkillUsageStore` appends a `SkillInvocationEvent` to a per-session JSONL file every time
`get_skill` is called:

```
{agentDataPath}/skill-usage/{sessionId}.jsonl
```

Each event records: `skillName`, `sessionId`, `timestamp`.

The dream cycle queries the last 30 days of events to:
- Annotate skills with usage counts in the consolidation prompt
- Build co-occurrence maps (skills used together in the same session)
- Identify sessions associated with poor quality signals

---

## Starter skills

`StarterSkillService` seeds the skill store at agent startup from registered
`IToolSkillProvider` implementations. These provide usage guides for built-in tool subsystems:

| Provider | Skill name | Contents |
|---|---|---|
| `SkillToolSkillProvider` | `skills-and-rules` | When and how to create, retrieve, and update skills |
| `MemoryToolSkillProvider` | `memory-systems` | How to use long-term, working, and conversation memory |
| `McpToolSkillProvider` | `mcp-tool-guide` | How to discover, invoke, and register MCP tools |

These are additive — existing skills are never overwritten on startup.

---

## Dream cycle — skill passes

The dream service (`DreamService`) runs four skill-related passes on each cycle:

### Pass 1 — Skill gap detection

Scans the conversation log for recurring request patterns the agent hasn't formalized into a
skill yet. Runs *before* consolidation so any new skills are included in the deduplication pass.

**Pattern-frequency signal:** The first user message from each session is tokenized and
cross-session term frequencies are computed. Terms appearing in 2+ sessions are injected into
the prompt as strong signals:

```
Recurring topics across sessions (term frequency ≥ 2 sessions):
- "email": 4 session(s)
- "summarize": 3 session(s)
```

The directive instructs the LLM to create a skill only when the same type of request appears 2+
times across sessions. High-frequency terms lower the threshold for proactive creation.

### Pass 2 — Skill consolidation

Reviews all skills for semantic overlap, near-duplication, and opportunities for abstraction.

**Inputs provided to the LLM:**
- All skills with content, usage counts (last 30 days), and co-occurrence annotations
- `[sparse-content: may need examples or steps]` annotation on skills with < 200 chars of content
  that were created more than 7 days ago
- Top 10 co-used skill pairs across sessions
- **Prefix cluster section** — skills grouped by name prefix:
  ```
  Skill name-prefix clusters:
  - 'mcp/*': mcp/calendar, mcp/email, mcp/weather
  ```

**What the LLM can do:**
1. Merge overlapping skills into improved combined ones
2. Create an abstract parent guide skill for prefix clusters — e.g. `mcp/guide` as a
   "when to use which" dispatch reference (conceptual, not procedural)
3. Populate `seeAlso` on any skill with related skill names (siblings, co-used, complements)
4. Delete redundant skills (exhaustive deletion contract: source names must appear in `toDelete`)

**Safety guard:** The service refuses to execute deletions if no replacement skills are being
saved — this prevents an LLM directive violation from silently destroying the skill library.

### Pass 3 — Skill optimization

Improves skills based on quality signals:

**At-risk skills** (existing behavior): Skills used in sessions that have Correction feedback or
poor/fair SessionSummary signals are rewritten with the failure context appended.

**Sparse skills** (new): Skills with < 200 chars of content and created more than 7 days ago are
included even without failure signals. Their section of the prompt is labeled:

```
## Skill: mcp/email [SPARSE]
...
### Review note: This skill has minimal content. Expand it with concrete steps, examples, and edge cases.
```

This ensures skills that are frequently recalled but never improved get expanded before they
cause problems.

### Pass 4 — Conceptual parent skill creation

Handled within the consolidation pass: when the prefix cluster section shows a cluster with 2+
members and no adequate guide exists, the LLM is instructed to create a `{prefix}/guide` skill
as a decision-tree / when-to-use reference.

Parent skills are **conceptual** (help the agent choose between siblings); leaf skills remain
**procedural** (step-by-step instructions for actually doing the thing).

---

## Configuration

```csharp
public sealed class SkillOptions
{
    public string BasePath { get; set; } = "skills";          // Relative to agent data path
    public string UsageBasePath { get; set; } = "skill-usage"; // Relative to agent data path
}
```

```csharp
public sealed class DreamOptions
{
    public string SkillDirectivePath { get; set; } = "skill-dream.md";       // Consolidation prompt
    public string SkillOptimizeDirectivePath { get; set; } = "skill-optimize.md"; // Optimization prompt
    public string SkillGapDirectivePath { get; set; } = "skill-gap.md";       // Gap detection prompt
    public bool SkillGapEnabled { get; set; } = true;
}
```

Custom directive files override the built-in fallbacks. Place them on the agent data volume at
the configured paths.

---

## DI registration

```csharp
builder
    .WithSkills()                    // FileSkillStore + FileSkillUsageStore + StarterSkillService
    .WithConversationLog()           // Required for skill gap detection
    .WithFeedback()                  // Required for skill optimization
    .WithDreaming()                  // Enables all dream passes
```
