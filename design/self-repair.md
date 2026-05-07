# Agent Self-Repair

## Problem

The agent currently exhibits a class of failure where a tool error gets misdiagnosed, the misdiagnosis is cached as if it were fact, and subsequent runs reinforce the false belief instead of correcting it. The recurring `calendar-mcp/get_calendar_events` "missing `timeZone`" failure is the visible symptom: every patrol calls the tool with no `arguments`, the MCP server reports the missing required parameter, the agent rationalises a structural cause ("the wrapper cannot pass arguments"), saves that rationalisation to working memory, and the next patrol injects the rationalisation as context and concludes the same.

Four reusable failure modes underlie this:

1. **Tool errors are read as stories, not data.** The literal recovery is in the error string but the LLM jumps to narrative.
2. **Capability claims are not falsifiable.** "Wrapper is broken" gets cached with no associated test that would prove it true or false.
3. **Dream-generated patterns can land on the wrong path.** The DreamService correctly identified the `timeZone` pattern but annotated wisp-generator skills, while the actual failures were direct `mcp_invoke_tool` calls.
4. **No closed loop.** Nothing checks whether a "fix" changed behaviour on the next run.

This design addresses all four. Point-fixing the calendar bug is explicitly a non-goal — it falls out of the general solution.

## Goals

- Tool errors that name what's missing are recovered without LLM narrative.
- Novel error shapes get an LLM-mediated fallback recovery on a cheap tier.
- Capability claims persisted across sessions carry a deterministic test that lets readers falsify them in seconds.
- Conflicting beliefs in memory are resolved at write time, not by recall ranking.
- The DreamService measures whether its repair changed observed behaviour and escalates when it didn't.
- Failure clusters are detected from telemetry, not waited for on a cron.

## Non-goals

- Replacing or restructuring general working memory. Capability claims are an additive curated subset.
- Autonomous user-visible action without verification. Every applied repair is verified or escalated.
- Eliminating the LLM from recovery. Determinism is the fast path; LLM is the fallback for novel shapes.

## Architecture overview

```
                 ┌──────────────────────────────────────────────┐
                 │                AgentLoopRunner               │
                 └────────────────────┬─────────────────────────┘
                                      │ tool calls
                                      ▼
                 ┌──────────────────────────────────────────────┐
                 │     mcp_invoke_tool (gateway, recovery)      │
                 │  ┌────────────────────────────────────────┐  │
                 │  │ Stage A: deterministic recovery        │  │
                 │  │   error pattern match → defaults reg.  │  │
                 │  └────────────────────────────────────────┘  │
                 │  ┌────────────────────────────────────────┐  │
                 │  │ Stage B: low-tier LLM fallback         │  │
                 │  │   constrained single-shot fill         │  │
                 │  └────────────────────────────────────────┘  │
                 └──────────────────────┬───────────────────────┘
                                        │ failure events (post-recovery)
                                        ▼
                 ┌──────────────────────────────────────────────┐
                 │  FailureClusterStore  (in-proc, PVC-backed)  │
                 └──────────────────────┬───────────────────────┘
                                        │ clusters
                                        ▼
                 ┌──────────────────────────────────────────────┐
                 │              DreamService                    │
                 │  - repair tickets                            │
                 │  - contradiction sweep                       │
                 │  - canary scheduling                         │
                 │  - existing dream passes (unchanged)         │
                 └──────────────────────────────────────────────┘

         ┌───────────────────────┐         ┌─────────────────────┐
         │   Capability claims   │         │      Canaries       │
         │  (claim/capability/*) │◄────────┤  /data/agent/       │
         │  internal API only    │ verify  │  canaries/<svr>/... │
         └───────────────────────┘         └─────────────────────┘
```

## Phase 1 — Mechanical recovery in the MCP gateway

### Where

Wraps `mcp_invoke_tool` at the gateway layer. Not in `AgentLoopRunner` — wisp `Direct` MCP steps benefit from the same recovery without going through the agent loop.

### Stage A — deterministic

Pattern-match common schema errors:

- `Required parameter '<X>'`
- `<X> is required`
- `missing required argument <X>`
- `expected field <X>`
- `<X>: must be provided`

On match, look up `<X>` in an `IToolArgumentDefaultsProvider` registry. Built-in providers:

| Provider | Resolves | Source |
|---|---|---|
| `TimeZoneDefaultProvider` | `timeZone`, `tz`, `timezone` | Same agent config the prompt builder reads |
| `CurrentTimeDefaultProvider` | `now`, `currentTime`, `referenceTime` | `DateTimeOffset.UtcNow` |
| `AccountIdFanoutProvider` | `accountId` for calendar-mcp | Calls `list_accounts`, returns `IEnumerable<string>` for fan-out |

Providers expose:

```csharp
public interface IToolArgumentDefaultsProvider
{
    bool CanResolve(string serverName, string toolName, string fieldName);
    Task<ResolvedDefault> ResolveAsync(ResolveContext ctx, CancellationToken ct);
}

public record ResolvedDefault(object Value, bool RequiresFanOut = false);
```

If a provider resolves, the gateway retries the call once with the field filled. On success, log a structured `auto-recovered` telemetry event (server, tool, field, provider, original error). On failure, fall through to Stage B.

`RequiresFanOut=true` means the provider returned a collection and the gateway issues N parallel calls, aggregating results.

### Stage B — low-tier LLM fallback

Used only when no deterministic provider resolved the field. Constrained single-shot prompt to Haiku (no tools, no narrative):

```
Tool: <serverName>/<toolName>
Required field: <fieldName>
Type: <jsonSchemaType>
Description: <fieldDescription>
Original call args: <existingArgs>
Return only a JSON value for <fieldName>.
```

Retry with the response. If still failing, surface to the agent with an annotated error including the recovery trail so it does not retry the malformed call. Exhausted recoveries also feed the FailureClusterStore (Phase 5).

### Acceptance

- A patrol that calls `get_calendar_events` with no arguments completes successfully on the next run after this phase ships, without skill or memory edits.
- Telemetry shows the `auto-recovered` event with `provider=TimeZoneDefaultProvider` and `provider=AccountIdFanoutProvider`.
- A canary with a deliberately-novel required field (synthetic test tool) is recovered via Stage B.

## Phase 2 — Falsifiable capability claims

### Scope

Adds a curated category, `claim/capability/...`, to long-term memory and working memory. The general WM API is unchanged. This subset carries strict policy.

### Write path

`SaveCapabilityClaim` is **internal only** — not exposed as an LLM tool. It is called by:

- The MCP gateway after exhausting recovery, when a tool consistently fails for reasons not tied to a single missing argument.
- The DreamService when promoting an observation to a claim.

Signature:

```csharp
record CapabilityClaim(
    string Server,
    string Tool,
    string Statement,        // "get_calendar_events fails for cross-account scans without explicit fan-out"
    VerifyShape Verify,      // structured call shape, NOT free text
    IReadOnlyList<string> Evidence,
    DateTimeOffset CreatedAt);

record VerifyShape(
    string Server,
    string Tool,
    JsonElement Arguments,
    VerifyExpectation Expect);   // Success | FailureWithMessage(pattern)
```

Writes without a `Verify` shape are rejected.

### Soft gate on regular WM writes

A regex/keyword filter on `SaveToWorkingMemory` and `SaveMemory` flags entries whose text contains capability-claim language ("blocked", "cannot", "wrapper limitation", "not supported", "does not expose"). The filter does not block the write; it tags the entry `kind=observation` and includes a hint in the tool result: "this looks like a capability claim — consider whether a verify shape exists; agent-self capability claims are downgraded to observations." LLM response to the hint is not required for correctness.

### Read-side filter

When WM injection assembles context for a session, entries in `claim/capability/*` go through a verifier:

- Run the `Verify` shape via the MCP gateway, time-budgeted (default 5s).
- Predicate succeeds → entry is **evicted** (deleted) from WM and not injected.
- Predicate fails → entry is injected as before.
- Predicate times out or errors unrelated to the claim → entry is injected with a `verifier-uncertain` annotation; not evicted.

This means capability claims have a half-life equal to the next session that triggers a verify, not a fixed TTL. Stale claims self-purge.

### Acceptance

- A claim "wrapper cannot pass arguments to get_calendar_events" with `Verify={call get_calendar_events with timeZone+accountId, expect Success}` is evicted on the next session that pulls it, after Phase 1 ships.
- An LLM-issued `SaveToWorkingMemory("calendar fresh-scan blocked")` is tagged as observation, not claim.
- The general WM API and existing entries are unaffected.

## Phase 3 — Contradiction detection on save

### Scope

Narrow: only `claim/capability/*` and `feedback/*`. Not all of WM. A general-purpose contradiction detector across all memory is out of scope and probably impossible without LLM judgment on every write.

### Detection

On save, scan existing entries in the same category with overlapping subject keys:

- Capability claims: same `(server, tool)`, opposite valence (`Statement` semantically inverts).
- Feedback: same rule subject, opposite directive (rare; mostly catches user reversals).

Matching is keyword-based, not LLM-based, in the hot path. Ambiguous cases skip the auto-resolution.

### Resolution

- Older entry receives `superseded_by=<new id>` and is excluded from search/recall.
- User-tagged corrections (entries with tag `correction` or category `feedback/from-user/*`) always win over agent-self entries regardless of recency.
- DreamService runs a contradiction sweep once per cycle as backstop, including LLM-mediated checks for cases the hot-path missed.

### Acceptance

- Saving "calendar wrapper does pass arguments" supersedes the older "wrapper cannot pass arguments" claim.
- Existing user-correction memories displace conflicting agent-self memories on the next sweep.

## Phase 4 — Closed-loop repair in DreamService

`RepairTicket` becomes a first-class dream artifact, persisted to PVC.

```csharp
record RepairTicket(
    string Id,
    FailurePattern Pattern,
    RepairTarget Target,
    JsonElement Change,            // shape depends on Target
    VerifyShape Verify,
    IReadOnlyList<RepairAttempt> Attempts,
    RepairStatus Status);          // Open | InProgress | Resolved | Escalated

enum RepairTarget {
    SkillBody,                     // edit a named skill
    WorkingMemoryEvict,            // drop a stale claim/observation by key
    ToolDefaultRegister,           // extend the Phase-1 defaults registry
    PromptBuilderHint              // append a pinned hint for a session category
}

record RepairAttempt(
    DateTimeOffset At,
    JsonElement AppliedDiff,
    VerifyResult Result);
```

### Pass per dream cycle

1. Read open tickets ordered by recency and severity.
2. For each: apply `Change` against `Target`, record an attempt.
3. Run `Verify`. Success → status `Resolved`. Failure → next attempt or escalate target type.
4. After `MaxAttempts` (default 3) failed attempts, status `Escalated` and write a summary to a `repair-escalations-latest` working-memory entry so user-facing sessions surface it.

### Target apply contracts

| Target | Apply | Idempotency |
|---|---|---|
| `SkillBody` | Read skill, apply structured edit (append/replace section), save. | Hash of resulting body. |
| `WorkingMemoryEvict` | Delete WM entries matching key pattern. | Idempotent by key. |
| `ToolDefaultRegister` | Append a config entry under `/data/agent/tool-defaults/<server>.json` consumed by Phase 1's registry. | Provider-name uniqueness. |
| `PromptBuilderHint` | Append a hint to `/data/agent/prompt-hints/<category>.md` injected by the prompt builder for that category. | Hint ID uniqueness. |

Skill edits are autonomous — DreamService already edits skills today, this extends that authority with verification.

### Acceptance

- A ticket targeting `SkillBody` for `calendar/mcp-calendar-operations-and-event-scanning` with a verify call that exercises `get_calendar_events` lands and is marked `Resolved` on the next cycle.
- A ticket whose verify keeps failing after 3 attempts shows up as `Escalated` in `repair-escalations-latest`.

## Phase 5 — Failure cluster store

### Storage

In-process for hot reads/writes, PVC-backed for crash recovery.

- Path: `/data/agent/telemetry/failure-clusters.jsonl` (append-only) plus a periodic snapshot at `/data/agent/telemetry/failure-clusters.snapshot.json`.
- In-memory: `ConcurrentDictionary<ClusterKey, FailureCluster>`.
- Periodic flush every N seconds and on graceful shutdown.
- On startup: load snapshot, replay JSONL appends since snapshot timestamp.

### Schema

```csharp
record ClusterKey(string Server, string Tool, string ErrorClass);

record FailureCluster(
    ClusterKey Key,
    int Count,
    HashSet<string> SessionIds,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<string> SampleErrorMessages);   // bounded
```

`ErrorClass` is derived deterministically from the error string by the same patterns used in Phase 1 (the field name from "Required parameter 'X'", or `unknown` if none matches).

### Triggers

- The MCP gateway records every failure post-recovery into the store. Auto-recovered calls go to a separate `auto-recovered` log, not the failure store.
- DreamService reads clusters on every cycle. Threshold for ticket creation: `Count >= 3 && SessionIds.Count >= 2 && LastSeen within 24h`.
- Tickets reference the cluster they came from, so subsequent occurrences increment the existing ticket rather than creating duplicates.

### Acceptance

- Three same-class failures across two sessions in 24h produce exactly one open ticket.
- Restarting the agent process preserves cluster state via PVC load.

## Phase 6 — Canaries

### Layout

```
/data/agent/canaries/
  <server>/
    <tool>.json     # canonical call shape, optionally with multiple variants
```

Each canary file:

```json
{
  "tool": "get_calendar_events",
  "variants": [
    {
      "name": "single-account-day",
      "arguments": { "accountId": "{{lhotka.net}}", "startDate": "{{today}}", "endDate": "{{today}}", "timeZone": "{{user.tz}}" },
      "expect": "success"
    }
  ]
}
```

Templates resolved against agent config and a small built-in token set (`today`, `now`, `user.tz`, account aliases).

### Runner

A background service in the agent process runs canaries on a slow cadence (default every 4h, configurable per server). Fixed call shapes run direct via the gateway with no LLM. Variants requiring synthesis use the same Phase-1 Stage-B path on Haiku.

Canary failures feed the FailureClusterStore as if they were real failures, with a `source=canary` tag.

### Reuse

The same canary file can serve as the `Verify` shape for Phase 2 capability claims and Phase 4 repair tickets. One library, three consumers.

### Acceptance

- Running canaries on a green system produces zero failures.
- Deliberately breaking a tool (e.g., revoking a credential) opens a ticket within one canary cycle.
- Canary cost on Low tier is bounded — empirical target: <$0.05/day across the full canary set.

## Sequencing & dependencies

| Phase | Depends on | Risk | Rough effort |
|---|---|---|---|
| 1. Mechanical recovery | None | Low — gateway-local | Small |
| 5. Failure cluster store | None (parallel with 1) | Low | Small |
| 2. Falsifiable claims | 1 (verify shapes need recovery to land) | Medium — touches WM injection | Medium |
| 3. Contradiction detection | 2 | Low — narrow scope | Small |
| 6. Canaries | 1, 5 | Low | Small |
| 4. Closed-loop repair | 5, plus 1/2/6 for verify shapes | Medium — multi-target apply contracts | Medium |

Suggested order: **1 + 5 in parallel → 2 → 6 → 3 → 4.** Phase 1 alone resolves the calendar incident. Phases 1, 2, 4 together give the property "agent self-repairs."

## Risks and open questions

- **Stage B LLM fill quality.** Haiku may produce wrong values for fields with subtle semantics. Mitigation: Stage B only runs when no deterministic provider exists; the result is verified by retrying the call (which either succeeds or feeds the cluster store).
- **Verify-predicate cost.** A capability-claim that triggers a verify on every session injection is expensive if the claim is hot. Mitigation: cache the verify result for a short TTL keyed by the verify shape hash; the cache is per-process and cheap.
- **Skill-edit churn.** Repair tickets editing skills could thrash skill bodies. Mitigation: dedup by `(target, change-hash)` before applying; drop attempts that propose the same change as a previous failed attempt.
- **PVC write rate.** The failure-cluster JSONL is append-only and could grow. Mitigation: snapshot rolls and JSONL truncation on snapshot completion.
- **Skill auto-edit safety.** Skill bodies drive future agent behaviour. A bad edit can cascade. Mitigation: each `SkillBody` change is reverted automatically if `Verify` fails, before the next attempt.

## Migration

- No migration required for existing memory or skills.
- Existing `claim/capability/*` entries (if any are written by hand or by future code) need verify shapes; until they have them, treat as observations.
- Existing dream passes are unchanged. Repair-ticket pass is additive.
