---
title: Dream service
nav_order: 11
---

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

Each dream cycle runs a log-retention pass followed by five knowledge passes in sequence.
Passes that depend on optional services (`IConversationLog`, `IFeedbackStore`,
`ISkillUsageStore`) are skipped when those services are not registered.

### The change gate

Most passes are *delta-driven*: they read the conversation log, a time-windowed slice of a
telemetry log, or a work queue, and return early when there is nothing new. An idle agent costs
nothing for those — the conversation log is cleared at the end of every cycle, so after a quiet
day it is empty and six passes no-op on the first line.

A handful are *corpus-wide* instead. Skill consolidation ships the whole skill catalog, graph
consolidation the whole graph, the contradiction sweep the whole claim/feedback corpus, identity
reflection its full experiential context. Ungated, these re-ask the model the same question about
the same bytes on every cycle forever, and the prompt scales with corpus size rather than with
how much the agent actually did. `DreamPassLedger` gates them: each hashes the corpus it is about
to describe and skips the LLM call when the hash matches what it last ran on.

Three details make it safe rather than merely cheap:

- **The fingerprint covers the corpus, not statistics derived from it.** Skill usage counts and
  co-occurrence tallies are 30-day rolling annotations that drift on their own as old events age
  out; importance scores are rewritten by the decay pass every cycle. Hashing those would mean
  nothing was ever "unchanged" and the gate would never fire.
- **`DreamPassMaxSkipInterval` (default 7 days) forces a run regardless.** Some directives are
  time-dependent in ways a content hash cannot see — graph consolidation prunes entities by
  staleness, so an untouched graph still becomes prunable through the passage of time. The floor
  bounds cost without switching such behaviour off.
- **The stamp is recorded only after the model returns usable JSON.** A failed or unparseable
  call is retried next cycle rather than being mistaken for a completed one.

The ledger lives at `{BasePath}/dream-pass-ledger.json` so the gate survives a restart. A missing
or corrupt ledger degrades to "run everything once", never to "skip something forever". Set
`DreamPassChangeGateEnabled: false` to restore the previous run-every-cycle behaviour.

Two related bounds close the same kind of leak elsewhere:

- **Tier routing review** reads its append-only log through a `TierRoutingReviewWindow` (default
  14 days). Without a window the pass always saw the same trailing 200 entries and could never
  run out of input.
- **Memory consolidation's duplicate-cluster carve-out** now skips clusters in which every member
  is already reviewed-and-unchanged. Such a cluster was, by construction, shown to the model
  together on the cycle that stamped it; re-offering it re-asks an answered question and quietly
  undid the reviewed-and-unchanged gate for exactly the entries most likely to sit in a cluster.
  One new or edited member re-opens the whole cluster.

### Pass 0 — Log retention

Runs **first and unconditionally**, before the knowledge passes — and crucially before the
"fewer than two memories → early return" guard, so the append-only logs are capped on every
cycle even when there is nothing to consolidate.

The agent's append-only JSONL telemetry logs (skill-usage, tool-call, feedback,
skill-resource-usage, wisp-executions) have no rotation of their own, so without this pass
they grow forever. The pass resolves every registered `IPrunableLog` and applies the
configured `LogRetentionPolicy`. Each log knows its own on-disk shape and delegates the file
work to the shared `JsonlLogRetention` helper:

| Log | Shape | Retention applied |
|---|---|---|
| skill-usage, tool-call, feedback | per-session directory of `{sessionId}.jsonl` | delete files older than `LogRetentionMaxFileAge` (by last-write time); cap the directory at `LogRetentionMaxFilesPerDirectory` (oldest dropped first); then line-trim each surviving file to `LogRetentionMaxLinesPerFile` under that session's write lock |
| skill-resource-usage, wisp-executions | single append-only file | trim to the last `LogRetentionMaxLinesPerFile` lines (atomic temp-file rewrite, serialized against the writer) |

Retention is best-effort: a failure pruning one log is logged and does not abort the sweep or
the rest of the dream cycle. A non-positive value disables the corresponding dimension.

The per-session line-trim is what bounds a *persistent* session file — `blazor-session.jsonl`,
`cli-session.jsonl` — that age/count pruning alone never reaps, because such a file is written
continuously (never aged out by last-write time) and is never the oldest file (never
count-pruned). On a long-running deployment the UI session's tool-call log is the largest single
file; line-trimming holds it to `LogRetentionMaxLinesPerFile`. Trimming reuses the store's own
per-session semaphore, so it can never race a concurrent append. (Scope matches age/count
pruning — top-level `{sessionId}.jsonl` files only; namespaced session files in subdirectories
are not swept.)

**Enabled/disabled by:** `DreamOptions.LogRetentionEnabled` (default `true`).

### Pass 0b — Archive purge

Hard-deletes memory entries archived longer ago than `DreamOptions.MemoryArchiveRetention`
(default 90 days). Runs before consolidation so the retention window is measured from the
archive event rather than from whatever the current cycle is about to archive.

Requires the store to implement `IArchivedMemoryMaintenance`; with a store that does not,
`ArchiveAsync` falls back to a hard delete and there is nothing to purge. A non-positive
retention keeps archived entries forever.

### Pass 1 — Memory consolidation

**Input:** a *gated subset* of long-term memory entries + recent feedback signals (last 7
days, up to 50). Each entry is rendered with its temporal context — `first=` (CreatedAt),
`last=` (LastSeenAt), `reinforced=N×` (ReinforcementCount), and `subject=...` when
subject-time metadata is present — so the LLM can reason about whether similar-sounding
entries describe the same durable fact or distinct moments.

**Candidate gating — what the LLM is allowed to see.** An entry becomes eligible only if it
is (a) new or changed since its last review, or (b) part of a near-duplicate cluster. Anything
else is withheld and therefore cannot be archived this cycle. Enforcement is in code, not just
in the prompt: the source lookup used for merge arithmetic is keyed on the eligible set, so an
ID the model invents or remembers from a previous cycle resolves to nothing.

This exists because exposure compounds. Handing over the whole corpus every cycle means every
entry is re-tried for deletion every cycle; at the default twice-daily cadence, a
one-in-a-thousand misjudgement per entry per cycle loses roughly half the corpus in a year.
Gating makes it roughly one decision per entry per content change.

- Review state is a content fingerprint (`consolidationReviewedHash` in entry metadata), so
  any write path — reinforcement, a tool edit, a prior merge — re-opens an entry for review,
  while importance decay (which changes score and `UpdatedAt`, not content) does not.
- Clustering comes from `IMemoryDuplicateCandidates` on the store: cosine over embeddings
  where available, Jaccard over content tokens otherwise, so BM25-only deployments still
  deduplicate. Controlled by `Dream:ConsolidationSimilarityThreshold` (default 0.88) and
  `Dream:ConsolidationMaxClusterSize` (default 3). The cluster cap bounds *eligibility*, not
  merge size — the model may propose a merge over any subset of what it is shown, and in
  practice does produce merges with more sources than this value. Large merges are constrained
  by the coverage check below rather than by a count.
- If the near-duplicate scan fails, the pass degrades to unreviewed-only rather than falling
  back to the whole corpus.

**What the LLM does:**
- Merges duplicate and near-duplicate entries into single improved entries, including
  widely-separated observations of the same durable fact (treating them as reinforcement,
  not novelty)
- Preserves topically-similar entries that describe distinct real-world moments (different
  trips, meetings, incidents) — especially when subject-time differs sharply
- Refines categories (e.g. promotes `general` entries to more specific categories)
- Flags noisy, low-value, or fully superseded entries for removal
- Mines `Correction` feedback for anti-patterns and writes them to `anti-patterns/{domain}`

**Temporal-field arithmetic on merge (computed by the host, not the LLM):**

- `CreatedAt` = `min(sources.CreatedAt)` (first-seen preserved)
- `LastSeenAt` = `max(sources.LastSeenAt)` (reinforcement, never reset to `now`)
- `ReinforcementCount` = `sum(sources.ReinforcementCount)`
- `Metadata` = subject-time keys merged via `MergeSubjectTimeMetadata` (start/end widen,
  point prefers most-specific); other metadata keys are dropped

This division keeps the LLM focused on *what to merge* and prevents dream housekeeping from
stamping every reprocessed entry as "just observed." See `dream.md` for the Temporal merging
rules the LLM is given.

**Removals are archived, never deleted.** Consolidation calls `ILongTermMemory.ArchiveAsync`,
which hides an entry from search while keeping it on disk and retrievable by ID. The archive
purge pass hard-deletes entries archived longer ago than `Dream:MemoryArchiveRetention`
(default 90 days), so a wrong merge or a wrong "ephemeral" call costs recall for a while
rather than costing the fact. Every archive is logged at Information with the entry's content
inline, which is what makes a bad cycle reviewable without restoring a volume backup.
`IArchivedMemoryMaintenance.RestoreAsync` puts an entry back.

**Merge coverage check.** Before a merge is applied, the specifics in its sources — proper
nouns, acronyms, and multi-digit numbers — must all appear in the merged text. If any is
missing the merge is rejected outright: nothing is saved and the sources are left alone. This
is what catches the characteristic failure, a plausible-reading merge that keeps the
machine-readable half of an entry and quietly drops a name or a date.

**Vocabulary is per-deployment.** Which capitalized words count as ordinary language rather
than as detail is not portable between agents, so it lives in
`merge-coverage-vocabulary.json` on the agent profile volume (next to `tier-selector.json`),
re-read at the top of every cycle:

```json
{
  "extraCommonWords":    ["briefing", "triage"],
  "alwaysSpecificWords": ["May", "Will", "Rose"]
}
```

`extraCommonWords` suppresses domain noise. `alwaysSpecificWords` reclaims words from the
built-in generic-English baseline and takes precedence over it — this matters most for agents
whose people or characters collide with ordinary English. The baseline contains `may`, `will`,
`some`, `first` and `last`, so a storytelling agent with a character named **May** or **Will**
should list them here. A malformed file falls back to the baseline with a warning; coverage
checking is never disabled by bad config.

**The two lists are scoped differently, on purpose.** A capitalized word is only evidence of a
proper noun when it is *not* sentence-initial, so:

| | Sentence-initial | Mid-sentence |
|---|---|---|
| Built-in baseline | applies | **does not apply** |
| `extraCommonWords` | applies | applies |
| `alwaysSpecificWords` | wins over both | wins over both |

The baseline is generic English that no operator chose, so applying it mid-phrase is what would
strip a character named May of protection, and what made `Personal`, `Class`, `Benefit` and
`Extended` too dangerous to add despite reading as noise — they name real things in *OneDrive
Personal*, *Blazor Online Class* and *MVP Azure Extended Benefit*. Position-scoping the baseline
protects those automatically and makes it safe to extend, which is why openers like `valid`,
`direct`, `alternative` and `through` are now in it.

`extraCommonWords` is the opposite: an explicit, corpus-specific judgement, so it applies in
every position. That is what lets a deployment suppress framing noise such as `Rocky`, which
appears mid-sentence (*"in Rocky's environment, calendar-mcp requires…"*) far more often than
not. The tradeoff is real — a word added here loses protection everywhere — so the guidance to
be conservative still stands. A name that must never be dropped goes in `alwaysSpecificWords`.

**Equivalent date spellings.** A month name is credited when the merged text carries the same
date numerically — source *"August 19, 2026"*, merge *"2026-08-19"* — and vice versa. This was
the single largest source of false rejections on a live corpus: `August` alone accounted for 13
of 70 rejections across eight cycles, every one a merge that normalized the date and kept the
day and year intact. Two guards keep it narrow: the month must appear adjacent to a day or year
*in the source* (so a person or release named August is never credited), and when the source's
date carries a year the numeric date must carry the same one. A merge that drops the date in
both spellings is still rejected.

Note the shape of both fixes: they widen what counts as *covering* a specific rather than
removing the requirement. Adding a word to a common list unprotects it everywhere, permanently;
an equivalence can only be satisfied by an equivalent form actually being present. Prefer the
latter — a false rejection is not one wasted merge but a duplicate cluster that re-proposes and
re-fails every cycle, one of which was observed rejected five times in eight cycles.

The check is deliberately biased toward rejection, because the costs are asymmetric: a false
rejection leaves a duplicate pair alive for another cycle, while a false acceptance destroys
the only record of how a fact was worded. Measured against a real 148-entry corpus, it rejects
0% of merges that preserve all source content and catches 83% of merges that drop a source
outright (the remainder being pairs where one source's specifics are a strict subset of the
other's — genuinely redundant). Known conservative false positives include 12h→24h clock
reformatting.

**High-value pruning floor.** Entries at or above `Dream:PruningProtectionImportance`
(default 0.80) or `Dream:PruningProtectionReinforcementCount` (default 5) can be merged, but
are never archived as standalone ephemeral. Merging preserves content and is covered by the
check above; ephemeral pruning discards a fact with nothing in its place, which is not
something to do on one model's say-so to an entry the agent has re-observed dozens of times.
This is a deterministic floor precisely because the prompt-level version did not hold —
`dream.md` already said reinforcement signals importance, and a live corpus still lost entries
reinforced 214, 106 and 80 times.

**Provenance.** Merged entries carry `mergedFrom` (source IDs) and `mergedAt` in metadata.
Source text is not duplicated: the sources are archived rather than deleted, so those IDs
resolve via `GetAsync` for the retention window. Metadata is not part of the search surface,
so this does not affect ranking. After the purge the IDs dangle by design.

**Ordering — replacement first, then retirement.** Merged entries are saved *before* their
sources are archived, and only sources belonging to a merge that actually persisted are
retired. A `toSave` entry with blank content is skipped with a warning and its sources are
kept. (The previous order deleted everything up front and saved afterwards, so a skipped or
failed save destroyed the sources outright.)

**Exhaustive-removal contract:** The union of explicit `toDelete` IDs and all `sourceIds`
referenced in *persisted* merged entries is archived. This prevents orphaned source entries
when the LLM omits IDs from `toDelete` but lists them in `sourceIds`. IDs outside the eligible
set are ignored.

**Episode reinforcement:** When a new session revisits an existing episodic memory (found by
the episode extraction pass), the existing entry is updated with `LastSeenAt = now` and
`ReinforcementCount += 1` — episodes that span multiple sessions accumulate reinforcement
just like durable facts.

**Directive file:** `dream.md` (relative to agent data path). Built-in fallback is used when
the file does not exist.

**Importance decay (runs before consolidation):** Entries whose `LastSeenAt` is older than
the configured grace period (default 30 days) have their importance multiplied by
`0.5^(elapsedDays / HalfLifeDays)` where `elapsedDays` is the calendar time since the
entry was last touched — exponential decay with a tunable half-life. With the defaults
(grace=30, half-life=45, floor=0.10), a core 0.95 memory reaches the 0.10 floor in
roughly 6 months; a 0.30 minor fact in ~3.5 months. The curve drops quickly at high
scores and asymptotes toward the floor, matching the "rapid drop after grace, then long
slow degradation" shape appropriate for a long-running agent.

**Calendar-time invariant.** Because multiplicative decay composes — `0.5^(a/T) · 0.5^(b/T)
== 0.5^((a+b)/T)` — running the dream cycle twice a day, once a day, or once a week all
produce the same calendar-time decay curve for a given half-life. No tuning is required
when changing `CronSchedule`.

Decay is keyed on `LastSeenAt` (real reinforcement) rather than `UpdatedAt`, so a recent
dream rewrite does delay the current pass's decay (since the `elapsedDays` clock is reset
by any record write) but does not permanently shield the entry — cumulative decay over
calendar time continues to accrue as long as `LastSeenAt` stays stale. The grace period
is bounded separately: first-past-grace decay applies only post-grace elapsed time, never
retroactively into the grace window.

All three parameters are tunable via `DreamOptions.ImportanceDecayGraceDays`,
`ImportanceDecayHalfLifeDays`, and `ImportanceDecayFloor`. Set `ImportanceDecayHalfLifeDays`
to zero or negative to disable decay entirely.

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

### Wisp failure analysis

**Input:** last 14 days of wisp execution records from `IWispExecutionLog`, grouped by
description to surface recurring patterns.

**What the LLM does:**
- Identifies recurring failure patterns (frequency ≥ 3) with classification and affected steps
- Proposes skill annotations — appends negative examples or corrected patterns to existing
  skills that generated broken wisp definitions
- Flags consistent success patterns (frequency ≥ 5, >80% success rate) as promotion candidates
  for stored wisp skills

**Directive file:** `wisp-failure-dream.md`. Built-in fallback if not present. Requires
`IWispExecutionLog` and `ISkillStore`. Enabled/disabled by
`DreamOptions.WispFailureAnalysisEnabled`.

See [Wisps — Dream-time learning](wisps.md#dream-time-learning-phase-5) for details.

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

    // Change gate — skip a corpus-wide pass whose input has not moved
    public bool DreamPassChangeGateEnabled { get; set; } = true;
    public TimeSpan DreamPassMaxSkipInterval { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan TierRoutingReviewWindow { get; set; } = TimeSpan.FromDays(14);

    // Append-only JSONL log retention (Pass 0)
    public bool LogRetentionEnabled { get; set; } = true;
    public TimeSpan LogRetentionMaxFileAge { get; set; } = TimeSpan.FromDays(30);  // per-session dirs
    public int LogRetentionMaxFilesPerDirectory { get; set; } = 1000;              // per-session dirs
    public int LogRetentionMaxLinesPerFile { get; set; } = 50_000;                 // single-file logs
}
```

In Kubernetes these are bound from the `Dream` configuration section via the agent ConfigMap
(`Dream__LogRetentionEnabled`, `Dream__LogRetentionMaxFileAge`,
`Dream__LogRetentionMaxFilesPerDirectory`, `Dream__LogRetentionMaxLinesPerFile`), driven by the
`agent.logRetention.*` Helm values. The Helm chart ships tighter, traffic-sized values than the
code defaults (`maxLinesPerFile: 10000` ≈ 11 MB for the wisp log at ~1.1 KB/line). Floor
`maxFileAge` at the widest dream query window (skill usage looks back 30 days) so age pruning
never starves a downstream pass.

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

Temporal fields (`CreatedAt`, `LastSeenAt`, `ReinforcementCount`) and subject-time
metadata are not part of the LLM response — they are computed by the host from the
source entries listed in `sourceIds`. The LLM is given these values as context on
input but does not emit them on output.

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
