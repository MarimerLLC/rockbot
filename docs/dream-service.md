# Dream service

The dream service is a background `IHostedService` that runs on a configurable timer to
autonomously refine the agent's accumulated knowledge — consolidating memory, improving skills,
inferring preferences, and detecting gaps — without any user interaction.

The key design principle: the dream cycle **refactors the knowledge graph**, it does not update
the agent's goals or system prompt. Every change it makes is to the persistent stores (memory,
skills) that get surfaced at runtime via BM25 recall.

---

## Scheduling

The service backs off if the LLM client is busy. While the LLM is processing a user request,
the dream cycle polls every 5 seconds and waits rather than queuing behind an active turn.

```
Startup
  └── InitialDelay (default 5 min)
        └── DreamCycle
              └── Interval (default 4 hrs)
                    └── DreamCycle
                          └── ...
```

---

## Passes

Each dream cycle runs five passes in sequence. Passes that depend on optional services
(`IConversationLog`, `IFeedbackStore`, `ISkillUsageStore`) are skipped when those services are
not registered.

### Pass 1 — Memory consolidation

**Input:** all long-term memory entries (up to 1000) + recent feedback signals (last 7 days,
up to 50)

**What the LLM does:**
- Merges duplicate and near-duplicate entries into single improved entries
- Refines categories (e.g. promotes `general` entries to more specific categories)
- Deletes noisy, low-value, or fully superseded entries
- Mines `Correction` feedback for anti-patterns and writes them to `anti-patterns/{domain}`

**Exhaustive deletion contract:** The union of explicit `toDelete` IDs and all `sourceIds`
referenced in merged entries are deleted. This prevents orphaned source entries when the LLM
omits IDs from `toDelete` but lists them in `sourceIds`.

**Directive file:** `dream.md` (relative to agent data path). Built-in fallback is used when
the file does not exist.

---

### Pass 2 — Skill gap detection

Runs **before** consolidation so any newly-created skills are included in the deduplication
pass that follows.

**Input:** full conversation log entries grouped by session + existing skill catalog

**What the LLM does:**
- Scans for recurring request patterns not covered by an existing skill
- Creates new skills only when the same type of request appears in 2+ sessions

**Pattern-frequency signal:** The first user message per session is tokenized and cross-session
term frequencies are computed. Terms appearing in 2+ sessions are injected as an explicit
signal:

```
Recurring topics across sessions (term frequency ≥ 2 sessions):
- "email": 4 session(s)
- "summarize": 3 session(s)
```

This gives the LLM a quantitative nudge — high-frequency terms indicate recurring needs the
agent should formalize.

**Directive file:** `skill-gap.md`. Built-in fallback if not present.

**Enabled/disabled by:** `DreamOptions.SkillGapEnabled` (default `true`). Requires
`IConversationLog`.

---

### Pass 3 — Skill consolidation

**Input:** all skills with content, plus:
- Usage counts per skill (last 30 days from `ISkillUsageStore`)
- `[sparse-content]` annotation on skills with < 200 chars of content older than 7 days
- Top 10 co-used skill pairs (skills invoked in the same session)
- Prefix cluster section — skills grouped by name prefix (`mcp/*`, `research/*`, etc.)

**What the LLM does:**
1. Merges semantically overlapping skills into improved combined ones
2. Detects prefix clusters and optionally creates abstract parent guide skills
   (e.g. `mcp/guide` — a "when to use which" dispatch reference for all `mcp/*` siblings)
3. Populates `seeAlso` on each skill with related skill names (siblings, co-used, complements)
4. Prunes skills that are clearly redundant

**Safety guard:** Deletions are refused if no replacement skills are being saved. An LLM that
proposes `toDelete` entries with an empty `toSave` is treated as a directive violation and the
entire consolidation is skipped.

**Metadata preservation:** Merged skills carry forward the earliest `CreatedAt` and most recent
`LastUsedAt` from their source skills.

**Directive file:** `skill-dream.md`. Built-in fallback if not present.

---

### Pass 4 — Skill optimization

Improves skills based on quality signals. Two types of skills are reviewed:

**At-risk skills** (failure-driven): Skills used in sessions that have:
- `Correction` feedback signals (explicit user corrections)
- `SessionSummary` feedback rated `poor` or `fair`

These are sent to the LLM with their associated failure context appended. The LLM is asked to
identify what step or gap likely caused the failure and produce an improved version.

**Sparse skills** (proactive): Skills with < 200 chars of content created more than 7 days ago,
even with no failure signals. These are sent with a structural review note:

```
### Review note: This skill has minimal content.
Expand it with concrete steps, examples, and edge cases.
```

This ensures skills that are frequently recalled but never improved get expanded before they
cause problems.

Skipped entirely if no at-risk or sparse skills are found.

**Directive file:** `skill-optimize.md`. Built-in fallback if not present. Requires
`ISkillUsageStore` and `IFeedbackStore`.

---

### Pass 5 — Preference inference

**Input:** full conversation log grouped by session + recent feedback signals (last 7 days)

**What the LLM does:**
- Identifies durable user preference patterns: formatting, tool corrections, communication
  style, topic clusters
- Applies sentiment-based thresholds before writing a preference:
  - Very irritated (repeated strong correction): 1 occurrence
  - Mildly frustrated (gentle pushback): 2 occurrences
  - Minor/casual suggestion: 3+ occurrences
- Writes preferences as long-term memory entries with `category: user-preferences/inferred`
  and `tags: ["inferred"]`
- Adds `metadata["requires_user_permission"] = "true"` for preferences touching security,
  credentials, or financial decisions

The conversation log is **always cleared** after this pass regardless of LLM success or
failure, to prevent unbounded growth.

**Directive file:** `pref-dream.md`. Built-in fallback if not present. Requires
`IConversationLog`. Enabled/disabled by `DreamOptions.PreferenceInferenceEnabled`.

---

## Directive files

Each pass has a corresponding directive file on the agent data volume. If the file does not
exist, a built-in fallback directive is used. Custom files override the built-in prompts
entirely — write a complete replacement, not a diff.

| File | Pass | Purpose |
|---|---|---|
| `dream.md` | Memory consolidation + anti-pattern mining | How to merge, categorize, and anti-pattern mine |
| `skill-gap.md` | Skill gap detection | When to create skills from conversation patterns |
| `skill-dream.md` | Skill consolidation | How to merge, abstract, and cross-reference skills |
| `skill-optimize.md` | Skill optimization | How to improve skills from failure context |
| `pref-dream.md` | Preference inference | How to infer and record preferences |

---

## Configuration

```csharp
public sealed class DreamOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(4);

    // Directive file paths (relative to agent data path)
    public string DirectivePath { get; set; } = "dream.md";
    public string SkillDirectivePath { get; set; } = "skill-dream.md";
    public string SkillOptimizeDirectivePath { get; set; } = "skill-optimize.md";
    public string PreferenceDirectivePath { get; set; } = "pref-dream.md";
    public string SkillGapDirectivePath { get; set; } = "skill-gap.md";

    // Feature flags
    public bool PreferenceInferenceEnabled { get; set; } = true;
    public bool SkillGapEnabled { get; set; } = true;
}
```

---

## DI registration

```csharp
builder
    .WithMemory()              // ILongTermMemory — required
    .WithSkills()              // ISkillStore + ISkillUsageStore — required for skill passes
    .WithConversationLog()     // IConversationLog — required for gap detection + preference inference
    .WithFeedback()            // IFeedbackStore — required for optimization + anti-pattern mining
    .WithDreaming(opts =>
    {
        opts.Interval = TimeSpan.FromHours(2);   // run more frequently
        opts.SkillGapEnabled = false;            // disable gap detection
    });
```

Each optional dependency is injected with a `? = null` default. The dream service degrades
gracefully — passes that need a missing service are simply skipped.

---

## LLM response format

All passes use a JSON response contract. The dream service extracts the outermost JSON object
from the LLM response, tolerating DeepSeek-style `<think>...</think>` reasoning blocks and
prose preamble.

**Memory consolidation / preference inference:**
```json
{
  "toDelete": ["id1", "id2"],
  "toSave": [
    {
      "content": "...",
      "category": "user-preferences/timezone",
      "tags": ["timezone"],
      "sourceIds": ["id1", "id2"]
    }
  ]
}
```

**Skill consolidation / optimization:**
```json
{
  "toDelete": ["skill-name-1"],
  "toSave": [
    {
      "name": "mcp/guide",
      "summary": "Choose between MCP email, calendar, and weather tools",
      "content": "...",
      "sourceNames": ["skill-name-1"],
      "seeAlso": ["mcp/email", "mcp/calendar"]
    }
  ]
}
```

**Skill gap detection:**
```json
{
  "toSave": [
    {
      "name": "summarize-emails",
      "summary": "Summarize an inbox digest into key action items",
      "content": "..."
    }
  ]
}
```
