# Reflex Tools

## Problem

A validated `SkillResource` — a wisp a subagent converged on and that has since succeeded three times across distinct sessions (`design/skill-asset-promotion.md`) — is fully trusted, but it is still *data*. Calling it means fetching the skill, finding the resource, and explicitly dispatching it through `spawn_wisps`. No agent can call it the way it calls a real tool: by name, with a schema, without first knowing which skill it lives under.

The cost of that gap compounds with use. Every call to a validated wisp still pays for the fetch-and-dispatch dance, and — more importantly — every call to a skill that *hasn't yet* been promoted pays the full cost of an LLM re-deriving a tool-call sequence it has already proven correct dozens of times. The pattern is structurally identical to the asymmetry `skill-asset-promotion.md` already names: a working pattern exists, but nothing makes it cheap to reuse. That document solved "capture the pattern." This one solves "make the captured pattern directly callable."

A second, related gap: `skill-asset-promotion.md` Phase 5 treats a failing provisional resource as disposable — two consecutive failures and it is deleted. That is the wrong response once a resource is directly callable with no LLM in the loop to notice and compensate for a bad call. RockBot already has a closed-loop repair mechanism (`design/self-repair.md`) built for exactly this shape of problem — diagnose, apply a correction, verify, escalate only after repeated failure — and it already has an applier for skill resources specifically (`SkillResourceApplier`, `RepairTarget.SkillResource`). It is simply not wired to this failure path yet.

## Goals

- A validated, `Wisp`-typed skill resource is callable as a first-class tool — discoverable, schema'd, zero LLM cost to replay — without changing how wisps execute or are sandboxed.
- A resource that stops being callable (demoted or deleted) is unregistered and evicted from every session holding it, with no stale tool left in anyone's list.
- A resource that starts failing is retrained before it is deleted: a corrected or re-earned version gets a chance to prove itself again, through the repair mechanism that already exists for this.
- Deletion is the last resort — it happens only after retraining has itself failed repeatedly, not on the first or second bad call.
- Tool registration stays cheap: a growing number of validated resources does not blow out every agent's tool list, the way an all-eager MCP typed-tool surface already proved it would.

## Non-goals

- Changing wisp execution, sandboxing, or process isolation. Only the calling convention changes — direct named call instead of fetch-then-spawn. Replay runs through the same wisp runner, unchanged.
- Eliminating the fetch-then-spawn path. It stays as the fallback whenever a resource has no registered tool (not yet validated, or mid-retraining).
- Covering `Python`-typed resources in this phase. A wisp replays calls through already-trusted executors; a promoted script is sandboxed code whose exposure changes meaningfully once it is callable from anywhere, not just via a subagent's explicit decision to run it. Tracked as a fast-follow once this ships and is proven on wisps.
- Unifying with `McpWrapperCatalog`/`McpTypedToolSurface` internals. That machinery fingerprints and reconnects a live downstream server connection; a reflex has none. A structurally identical, independent sibling is cheaper to build and reason about than forcing a reflex to fit a connection-shaped abstraction it doesn't have.
- Retroactively reflex-ifying resources deleted under the old demotion rule before this ships.

## Architecture overview

```
Skill store (validated, Wisp-typed SkillResource)
        │ watches
        ▼
 ReflexToolCatalog ──builds──> ToolRegistration + ReflexExecutor
        │                              │
        │ unregister on demote/delete  │ replays via the existing wisp runner
        ▼                              ▼
 ReflexToolSurface (session LRU + namespace pinning)
        │ joins ChatOptions.Tools when activated
        ▼
   Agent's tool list — callable by name, zero LLM replay cost

 Failure path (replaces "2 fails → delete"):
   ReflexExecutor failure ──feeds──> FailureClusterStore (existing)
        │
        ▼
   RepairTicket(Target=SkillResource)  [self-repair.md, already built]
        │  new directive decides the op, evidence-based:
        ├─ demote-provisional  (cause unknown — re-earn trust, no content change)
        ├─ attach               (cause evident — corrected content, re-earn trust)
        └─ after MaxAttempts (default 3) failed repair attempts → Escalated
                │
                ▼
          delete (real demotion — now the last resort, not the first response)
```

This design is additive on top of two already-shipped systems: `skill-asset-promotion.md`'s provisional/validated trust gradient, and `self-repair.md`'s ticket/apply/verify loop. Nothing here changes either system's own contract; it redirects one failure path in the first, and adds one new consumer (a new directive) that feeds the second.

## Phase 1 — Redirect the failure path through repair

### Where

`design/skill-asset-promotion.md` Phase 5 (the provisional validation/demotion rule), `src/RockBot.Host/DreamService.cs` (dream-pass wiring), a new directive `src/RockBot.Agent/agent/skill-resource-repair-dream.md`.

### Change

Add an Amendment to `skill-asset-promotion.md` (see that file) redirecting its Phase 5 failure rule: a cluster-worthy failure (the existing `FailureClusterStore` threshold, `Count >= 3 && SessionIds.Count >= 2 && LastSeen within 24h`) opens or advances a `RepairTicket` with `Target=SkillResource` instead of deleting the resource directly. `SkillResourceApplier` already supports the three operations this needs — `demote-provisional`, `attach`, `delete` — so no change to the applier itself.

What's missing is the directive that chooses between them. Add `skill-resource-repair-dream.md`, shaped like the existing `wisp-success-dream.md`/`wisp-failure-dream.md` pair: given `FailureClusterStore` evidence for a `(skill, resource)` pair, it picks an operation — evidence-based, so it skips straight to `attach` (or `delete`) when the cause is already evident (e.g. a detected tool-fingerprint drift), and falls back to the cheap `demote-provisional` only when the cause isn't yet known — and for `attach`, produces the corrected wisp body. `DreamService` needs the same read-directive-output → create-or-advance-ticket → apply → verify wiring `wisp-success-dream.md`'s promotions already have. `delete` only fires once a ticket reaches `Escalated` (`RepairTicketOptions.MaxAttempts`, default 3, unchanged).

### Acceptance

- A failure cluster on a provisional *or* validated resource opens a `RepairTicket` rather than deleting the resource directly.
- `demote-provisional` is chosen when the directive has no positive evidence of cause; `attach`/`delete` when it does.
- A resource is deleted only after its ticket reaches `Escalated`.
- This phase ships value with zero reflex tools registered anywhere — every existing skill resource gets a retraining chance before deletion, independent of Phase 2.

## Phase 2 — `ReflexToolCatalog` and `ReflexExecutor` (Wisp-only)

### Where

New project `src/RockBot.Tools.Reflex/`, sibling to `src/RockBot.Tools.Mcp/`.

### Change

`ReflexToolCatalog` watches `ISkillStore`. For every validated (`Provisional=false`), `Wisp`-typed resource, it builds a `ToolRegistration` — name `reflex__{namespace}__{resource}`, schema from the wisp's declared parameters — and a `ReflexExecutor`, then calls `IToolRegistry.Register`, the same in-process path `WispToolRegistrar`/`DreamCostToolRegistrar` already use. `"reflex"` becomes a reserved word no MCP server name may use, so the two naming schemes can never collide (MCP server names already can't contain `__`).

The moment a resource stops being validated — demoted back to provisional by Phase 1's repair path, or deleted — the catalog unregisters its tool and evicts it from every session holding it active or pinned, reusing the eviction hook `McpWrapperCatalog` already has for a vanished or changed MCP tool.

`ReflexExecutor` looks up the stored wisp body and replays it through the existing wisp runner — the same one `spawn_wisps` uses. It must keep emitting the `(skillName, filename, succeeded)` event the Phase 1 repair/validation read-path depends on: bypassing fetch-then-spawn must not also bypass the instrumentation that keeps the trust gradient honest. In-flight calls lease their executor snapshot (mirroring `McpServerConnections`' lease pattern), so a concurrent demotion can't invalidate a call already running — the demotion takes effect on the next call instead. Invocations are tagged `via=reflex` in the existing tool-invocation metrics, the same way MCP calls are already tagged `via=wrapper|invoke_tool`.

### Acceptance

- A validated Wisp resource is registered as a callable tool within one reconciliation cycle.
- A demoted-to-provisional or deleted resource is unregistered and evicted from every session holding it active or pinned.
- A direct reflex call still records the validation event; a reflex call appears in metrics tagged `via=reflex`.
- A demotion racing an in-flight call does not fail the call in progress.

## Phase 3 — `ReflexToolSurface` (lazy activation, namespace pinning)

### Where

New file(s) in `src/RockBot.Tools.Reflex/`, sibling to `McpTypedToolSurface`.

### Change

Per session, a bounded LRU activation list — proposed default 40, mirroring `MaxActivatedToolsPerSession` — where re-using a reflex keeps its place instead of resetting it. Independent of MCP's own activation budget: different kinds of surface, and sharing slots risks one starving the other.

Namespace pinning: touching one reflex under a skill namespace pins every validated reflex in that namespace into the session's list, bounded by a separate small cap — proposed default 3, mirroring `MaxPinnedServersPerSession`.

Discovery rides the existing skill-recall (BM25) search the LLM already uses, not a new search tool and not `mcp_find_tools` — reflexes are children of skills, not of MCP servers. When skill search surfaces a skill with validated reflex resources, they show as directly callable there.

### Acceptance

- Namespace pinning activates every sibling reflex in the touched namespace, bounded and LRU-evicted at the pin cap.
- Re-activating a reflex keeps its place in the session's list rather than resetting it.
- Skill search that surfaces a skill with validated reflexes shows them as directly callable.

## Phase 4 — Python-typed reflexes (fast-follow)

### Where

Extends `src/RockBot.Tools.Reflex/` once Phases 1–3 are proven on wisps.

### Change

Extend `ReflexToolCatalog`/`ReflexExecutor` to cover `Python`-typed resources. Open question to settle when this phase starts: should the trust bar before registering a Python reflex be higher than a wisp's (e.g. more successful validations), given it's sandboxed code rather than a replay of already-trusted tool calls?

### Acceptance

To be defined when this phase is scoped, after Phase 2's `via=reflex` metrics and Phase 1's repair-ticket data give a real baseline to set the bar against.

## Sequencing & dependencies

| Phase | Depends on | Risk | Rough effort |
|---|---|---|---|
| 1. Redirect failure path | None | Medium — new directive + DreamService wiring | Medium |
| 2. Catalog + executor | 1 (so a reflex that fails has somewhere to go other than deletion) | Low — reuses existing registry/executor patterns | Medium |
| 3. Tool surface | 2 | Low — mirrors a proven pattern | Medium |
| 4. Python fast-follow | 2, 3 | Medium — new trust-bar question | Medium |

Suggested order: **1 → 2 → 3 → 4.** Phase 1 alone improves every existing skill resource, reflex-exposed or not, and ships independently of the rest.

## Risks and open questions

- **Repair-pass misjudgment.** The new directive could pick `attach` with content that's still wrong, or `demote-provisional` repeatedly on a cause `attach` would have fixed faster. Mitigation: `RepairTicket`'s existing attempt/verify loop already bounds this — a wrong choice just consumes one of `MaxAttempts` before escalating.
- **Namespace pin granularity.** Skill namespaces vary in size; pinning a large one could fill the activation budget in one step. Mitigation: the activation budget (40) caps the total regardless of how a namespace pin fills it; revisit the namespace-pin-count default (3) empirically once real usage data exists, the way `worker-subagents.md` treats its own defaults.
- **Two independent LRU/pin systems to reason about** (MCP's and reflexes'). Mitigation: kept deliberately structurally identical so the mental model transfers directly; revisit merging only if usage shows no cross-pressure risk.
- **Reserved-word collision.** `"reflex"` as a reserved MCP-server-name prefix needs enforcement at server registration time, not just at reflex-tool naming time, or a future `mcp_register_server` call could create an ambiguous name.

## Migration

- No migration required for existing skills, memory, or wisps. `SkillResource`'s schema is unchanged; `Provisional`, `CreatedAt`, and the manifest shape already carry what Phase 2 needs to read.
- `RepairTarget.SkillResource` and `SkillResourceApplier` already exist and are unchanged by this work; only the trigger path into them changes (Phase 1).
- Resources already deleted under the old two-strikes rule are not retroactively restored.
