# Skill Asset Promotion

## Problem

The agent has a self-improvement loop, but it is asymmetric: failures get distilled into prose annotations on skills, while successes have no path to becoming a saved, reusable asset. The visible symptom is the 10am operational-review subagents repeatedly spending 8–10 minutes on schema discovery, tool-name reconciliation, and wisp-aggregation retries that converge on a working pattern — and then throw the working pattern away when the session ends. The next day's run starts from zero.

Four reusable defects underlie this:

1. **Wisp success bodies are not retained.** `WispExecutionRecord` (`src/RockBot.Host.Abstractions/WispExecutionRecord.cs`) stores `DefinitionHash` (SHA-256 of step definitions) but not the step definitions themselves. Even when a hash repeats successfully, the JSON to save as an attachment is unrecoverable from the log.
2. **The promotion path in the dream is dead-ended.** `wisp-failure-dream.md` outputs `promotionCandidates` with frequency-and-recommendation strings, but the receiver in `DreamService.cs:2739` only logs them. The DTO carries no `targetSkillName`, no `resourceFilename`, no body; nothing calls `SaveSkill` with `resources=[…]`. Compare to `skillUpdates`, which actually mutates skill content.
3. **The whole reflection system is failure-shaped.** `skill-optimize.md` fires on sessions with corrections or retries. `wisp-failure-dream.md` is named for failure. There is no symmetric "what worked unusually well today" pass. The result is skills like `patrol/wisp-mcp-params` accumulating ten near-identical "negative example" paragraphs instead of one positive example file attached as a resource.
4. **Subagents promote prose, not assets.** `subagent-directives.md` instructs the subagent to call `save_skill` to replace ambiguous lines with verified specifics — text edits only. Nothing tells the subagent to attach the working wisp/script body it just converged on as a typed `SkillResource`. `SkillTools.SaveSkill` already accepts a `resources` parameter, but the directive does not exercise it.

The infrastructure is fully wired end-to-end (`SkillResource`, `Manifest`, `SaveSkill(resources=…)`, `GetSkillResource`, the `[Wisp, Python]` index tag, the `Wisp` enum value in `SkillResourceType`). The capability is unused because nothing tells the agent — in-session or in-dream — to use it.

## Goals

- A subagent that converges on a working tool sequence captures it as a typed skill resource before exiting, with a verify shape that lets the next session prove it still works.
- The dream system distinguishes failure-shaped reflection (already present) from success-shaped reflection (new), and promotes repeating successful patterns into skill resources autonomously.
- Promoted resources self-evict when they stop working, without manual cleanup.
- The skill index visibly distinguishes provisional (one-shot in-session) resources from validated ones, so the LLM and humans can see the trust level.

## Non-goals

- Replacing the existing failure-driven `skill-optimize` or `wisp-failure-dream` passes. They keep doing what they do; this work adds the missing complement.
- General-purpose code generation. Promotion captures *observed* working assets — wisp JSON the subagent already executed, scripts it already ran. Nothing is synthesized from scratch.
- Retroactive promotion of historical successes from before this work ships. The execution log shape changes; old hash-only records remain hash-only.

## Architecture overview

```
                  ┌──────────────────────────────────────────────┐
                  │   Subagent loop (during task execution)      │
                  │   on convergence → call promote_skill_asset  │
                  └────────────────────┬─────────────────────────┘
                                       │ provisional resource
                                       ▼
        ┌────────────────────────────────────────────────────┐
        │   Skill store: <skill>.resources/<file>            │
        │   manifest entry tagged provisional=true           │
        └────────────────────┬───────────────────────────────┘
                             │
        ┌────────────────────┼─────────────────────────────────┐
        │                    │                                 │
        ▼                    ▼                                 ▼
 ┌────────────┐    ┌─────────────────────┐         ┌────────────────────┐
 │ Next call  │    │ Wisp execution log  │         │   DreamService      │
 │ exercises  │    │ now stores body     │         │   (success-pass)    │
 │ verify     │    │ for repeat hashes   │         │   promotes repeats  │
 │ → keep or  │    └─────────────────────┘         │   → SaveSkill(...)  │
 │   demote   │                                    └────────────────────┘
 └────────────┘
```

This intentionally mirrors the structure of `design/self-repair.md`. Where they overlap, the asset-promotion track reuses self-repair primitives:

- The verify-shape concept from self-repair Phase 2 (capability claims) describes the same predicate-driven eviction we want for provisional resources.
- The `RepairTarget.SkillBody` apply-and-verify machinery in self-repair Phase 4 extends naturally to a `RepairTarget.SkillResource` target (text-edit becomes resource-attach).
- The FailureClusterStore from self-repair Phase 5 is the natural home for "this provisional resource broke" telemetry.

## Phase 1 — Persist wisp step bodies for promotion

### Where

`src/RockBot.Host.Abstractions/WispExecutionRecord.cs` and the implementation behind `IWispExecutionLog`.

### Change

Extend `WispExecutionRecord` with one new field:

```csharp
/// <summary>
/// JSON-serialized step definitions for this run, retained when the run succeeded
/// and promotion would be possible. Null for failed runs and for records older
/// than the retention window.
/// </summary>
public string? DefinitionBody { get; init; }
```

Storage policy:
- Successful runs: keep the body for at least the dream-cycle window (default 7d).
- Failed runs: do not retain the body — the failure path already captures error context.
- Per `definitionHash`: dedup to one canonical body. Subsequent successes increment a counter on the canonical entry rather than re-storing the same JSON.
- Bound the per-record body size (e.g. 8 KiB); larger wisps are recorded with body=null and a note. In practice the wisps we want to promote are small.

The dream pass and any future success-pass call `IWispExecutionLog.GetCanonicalBodyAsync(definitionHash)` to recover a body for promotion.

### Acceptance

- A successful wisp run writes `DefinitionBody` populated; a failed run writes it null.
- Two consecutive successes of the same `definitionHash` produce one stored body and an incremented count, not two stored bodies.
- A wisp body that exceeds the cap is stored with body=null and a `body-omitted-too-large` flag, and the dream pass treats it as ineligible for promotion.

## Phase 2 — In-session promotion by subagents

### Tool

A new LLM-callable tool on `SkillTools`:

```csharp
[Description(
    "Save a working asset (wisp definition, script, schema) you just verified " +
    "as a resource attached to the skill that guided you. Use this only after " +
    "a tool-call sequence has actually executed successfully — never speculatively. " +
    "The resource is marked provisional until validated by future runs.")]
public Task<string> PromoteSkillAsset(
    string skillName,
    string filename,
    SkillResourceType type,
    string content,
    string? verifyHint = null);
```

Internally this is thin glue over the existing `SaveSkill(name, content, resources)` path: it reads the current skill, appends or replaces a single resource entry, and saves. The new entry's manifest item carries `provisional=true` and a timestamp. The skill body is not modified.

### Directive update

Append a section to `src/RockBot.Agent/agent/subagent-directives.md` titled "Capture working assets as skill resources" with:

- Trigger: a tool sequence converged on a working shape after non-trivial discovery (schema confusion, tool-name reconciliation, parameter-shape iteration).
- Action: call `promote_skill_asset` with the **exact** wisp/script body that just succeeded, attached to the skill that guided the session.
- Constraints: only after observed success, never speculatively; one asset per concrete pattern; use the existing skill name (do not invent a new skill — promotion attaches to an existing skill, optimization edits prose).

This is the symmetric complement to the existing "Tighten skills when you verify their ambiguities" section, which only covers prose.

### Manifest extension

`SkillResource` gains two optional fields:

```csharp
public sealed record SkillResource(
    string Filename,
    SkillResourceType Type,
    string Description,
    bool Provisional = false,
    DateTimeOffset? CreatedAt = null);
```

`SaveSkill`'s formatted output and `SkillTools.FormatResourceTag` show provisional resources distinctly (e.g. `[Wisp*, Python]` where the asterisk denotes at least one provisional entry).

### Acceptance

- A subagent that ran a successful wisp can call `promote_skill_asset` and the resource appears under `<skill>.resources/<filename>` on disk with `provisional=true` in the manifest.
- The skill index after promotion shows the new resource tag.
- A subsequent `get_skill` reveals the manifest entry and the asterisk; `get_skill_resource` returns the body.

## Phase 3 — Success-shaped dream pass

### Directive

Add `src/RockBot.Agent/agent/wisp-success-dream.md` modelled on `wisp-failure-dream.md`. Input: recent execution records grouped by `(definitionHash, invokingSkill?)`. Output:

```json
{
  "promotions": [
    {
      "targetSkill": "calendar/mcp-calendar-operations-and-itinerary-retrieval",
      "filename": "scan-events-fan-out.json",
      "resourceType": "Wisp",
      "description": "Per-account get_calendar_events fan-out with timeZone and accountId",
      "definitionHash": "abc123…",
      "frequency": 6,
      "successRate": 1.0
    }
  ]
}
```

Inclusion thresholds: `frequency >= 3 && successRate == 1.0` initially (tighter than the failure pass; we want zero false positives more than we want recall).

### Receiver wiring

`DreamService` gains a success-pass method that:

1. Calls `IWispExecutionLog.QuerySuccessfulHashesAsync(window)` to gather candidate hashes.
2. Resolves the `invokingSkill` for each (already known: subagents/sessions log which skill was active when the wisp ran; if not, this is the first thing to add — a small free addition that benefits other passes too).
3. Asks the LLM to choose promotions per the directive.
4. For each promotion: calls `IWispExecutionLog.GetCanonicalBodyAsync(definitionHash)` (Phase 1) to obtain the body, then calls `SaveSkill(name, content=existing, resources=[SkillResourceInput(...)])` to attach it. Marked **non-provisional** because the dream pass operates on observed repetition.

### Acceptance

- A wisp body that has succeeded ≥3× across ≥2 sessions in the window is promoted to a non-provisional resource on the relevant skill on the next dream cycle.
- A wisp body that succeeded once is not promoted by the dream pass (in-session promotion is the path for one-shot capture).
- The dream-cycle log includes a `promotions: N attached` summary line.

## Phase 4 — Wire `promotionCandidates` and migrate to `promotions`

The existing `WispPromotionCandidateDto` in `DreamService.cs:2774` is advisory-only. It is replaced (or deprecated alongside) by the actionable `Promotion` shape from Phase 3. The receiver loop at `DreamService.cs:2739` that currently only logs is replaced by the resolver-and-attach loop described above.

This is a small, mechanical phase, but it is called out separately because it is the cleanup that closes the existing dead end.

### Acceptance

- After this phase ships, no log line of the form `wisp promotion candidate — … (freq=N): <recommendation>` exists without a corresponding attach attempt.
- A telemetry counter `dream.wisp_promotions_attached` increments on each successful attach.

## Phase 5 — Provisional resource validation and demotion

### Read path

When a session uses a skill resource (via `get_skill_resource` or by a wisp run that loaded the resource), the runner records a `(skillName, filename, succeeded)` event. For provisional resources, this is the validation signal.

### Validation policy

- Provisional resource succeeds **N** times (default 3) across distinct sessions → manifest entry flipped to `provisional=false`.
- Provisional resource fails **M** times (default 2) consecutively → manifest entry removed, body deleted, and the failure feeds the FailureClusterStore (self-repair Phase 5) so the dream pass can decide whether to also weaken the originating skill prose.
- Provisional resources older than 30d with no usage are demoted (body kept, manifest entry annotated `stale`) so the LLM stops loading them.

### Verify hint

The optional `verifyHint` from Phase 2 is attached to the manifest entry as advisory free text — the LLM sees it via `get_skill` and can use it when deciding whether the resource is appropriate for a given task. (We avoid baking a structured verify-shape language into Phase 2 because that's exactly what self-repair Phase 2 is solving for capability claims; once that's done, asset verify shapes can adopt the same primitive.)

### Acceptance

- A provisional resource that runs successfully 3× in distinct sessions is marked non-provisional automatically.
- A provisional resource that fails twice in a row is removed, body and manifest entry both cleaned up, and a failure-cluster entry is created.
- A 31-day-old unused provisional resource is annotated stale and de-emphasized in the index.

## Sequencing & dependencies

| Phase | Depends on | Risk | Rough effort |
|---|---|---|---|
| 1. Persist wisp bodies | None | Low — additive field | Small |
| 2. In-session promotion tool + directive | None (parallel with 1) | Low | Small |
| 3. Success-shaped dream pass | 1 | Medium — new directive + dream wiring | Medium |
| 4. Wire `promotionCandidates` actionable | 1, 3 | Low — mechanical | Small |
| 5. Provisional validation/demotion | 2 | Medium — read-side instrumentation | Medium |

Suggested order: **1 + 2 in parallel → 3 → 4 → 5.** Phase 2 alone gives the calendar lane a way to capture today's work today. Phases 1–4 close the dream-side loop. Phase 5 turns on the trust gradient.

## Risks and open questions

- **Promoted-asset rot.** A wisp that worked yesterday may break when the underlying MCP tool changes. Mitigation: Phase 5's failure-driven demotion plus the existing FailureClusterStore. The provisional flag also signals "trust this less" until validated.
- **Promotion churn from minor variants.** Two wisps that differ only in a date string will hash differently and look like distinct patterns. Mitigation: the Phase 3 directive promotes only on `frequency >= 3` *and* high success rate, so one-off variants are filtered. In-session promotion (Phase 2) accepts the cost of one-off captures intentionally — the demotion path cleans up.
- **Skill-attachment storage growth.** Each promotion adds bytes on the PVC. Mitigation: 30d staleness sweep in Phase 5; bounded body size in Phase 1; expected steady-state is dozens of resources, not thousands.
- **Subagent over-promotion.** An LLM that tries to attach assets aggressively could spam the skill library. Mitigation: directive constraint ("only after observed success, never speculatively"); read-side validation removes garbage within a few sessions; `provisional` tag visible in the index.
- **Hash collision on body retention.** Phase 1 dedups by `definitionHash` but the canonical body might drift if two semantically-equivalent-but-differently-formatted wisps hash the same after normalization. Mitigation: dedup is on the unmodified hash that's already used elsewhere; if drift becomes real we add a normalized-hash side index.
- **Overlap with self-repair Phase 4 (`SkillBody` target).** That phase already plans to autonomously edit skill bodies. We should land Phase 4 of this design *after* self-repair Phase 4 if both are in flight, so the apply-contract for `SkillResource` can be added to the same `RepairTarget` enum rather than forking.

## Migration

- No migration required for existing skills, memory, or wisps.
- The `WispExecutionRecord.DefinitionBody` field is nullable; old records remain valid with `null` and are simply ineligible for promotion. New records carry bodies forward.
- The `provisional` and `createdAt` fields on `SkillResource` are optional; the one existing populated manifest (`patrol/wisp-mcp-params`) reads back unchanged with both fields default-null.
- `WispPromotionCandidateDto` is kept as a deprecated alias for one release cycle to avoid breaking any in-flight log consumers, then removed.
