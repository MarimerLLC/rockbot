# Scheduled Task Directives, and Convention-Aware Skill Consolidation

## Problem

Two distinct design errors are causing the DreamService to silently fight
the rest of the system on every consolidation pass:

1. **The patrol's task definition is wedged into the skill store.** The
   heartbeat patrol's evolving checklist of "what to check on each run"
   lives as `patrol/proactive-actions` in the skill store, edited by the
   agent via `save_skill` and loaded by the directive via
   `get_skill("patrol/proactive-actions")`. But this content isn't a
   reusable capability — it's *the patrol's own task instructions*, a
   directive whose body happens to evolve over time. Calling it a "skill"
   exposes it to skill-consolidation passes that have no business
   touching scheduled-task definitions, and is the entire reason
   `ProtectedSkillPrefixes = ["patrol/"]` exists (PR #275). The
   protection then over-shoots and keeps ~19 unrelated drift skills
   under `patrol/*` alive against consolidation forever.

2. **The dream's consolidation prompt asks the wrong question of
   `mcp/*` skills.** `DreamService.cs:686-703` surfaces every name-prefix
   cluster to the LLM with the question *"consider whether each cluster
   warrants an abstract parent guide skill"* and offers cross-suffix
   merges as a normal output. That phrasing is correct for *topical*
   clusters (`coding/*`, `email/*`) where parent extraction or merging
   redundant variants is genuinely useful. It is structurally wrong for
   `mcp/{server-name}`, where each suffix is a 1:1 binding to a live
   external MCP server. Merging `mcp/ms365` and `mcp/github` into
   `mcp/microsoft-services` doesn't compress redundancy — it destroys a
   live binding that the agent will rebuild on its next encounter with
   the ms365 server, producing a permanent dream-vs-agent oscillation.

Both errors are architectural: the system has no notion of a *task
directive* as distinct from a *skill*, and no notion of a *naming
convention's consolidation semantics*. The current `patrol/` prefix
protection plasters over (1); nothing addresses (2) yet.

Skill *name* stability is largely a non-issue independent of these two
fixes, because skills are normally discovered through the session-start
**skill index** plus **BM25 recall** in `AgentContextBuilder` (lines
417, 436, 450), not through hardcoded names the agent carries between
sessions. As long as consolidation produces summaries that preserve the
search-relevant keywords of the originals, a renamed skill is still
findable. So we do not need a pin registry, an alias-on-merge mechanism,
or any general-purpose name-protection contract.

## Goals

- Scheduled task definitions persist outside the skill store, so dream
  consolidation cannot touch them and no protection mechanism is needed
  to keep them alive.
- The dream understands which name-prefix conventions represent
  namespaced singletons bound to external entities, and does not propose
  cross-suffix merges or parent-guide extraction for them.
- `ProtectedSkillPrefixes` is removed. With (1) and (2) in place, no
  skill needs blanket prefix-based protection.
- Existing skill discovery via index + BM25 keeps working unchanged;
  the merged-skill-summary expectation (preserve search-relevant
  keywords from originals) is made explicit in the consolidation
  directive.
- Drift skills currently piling up under `patrol/*` are released to the
  next consolidation pass and cleaned up naturally.

## Non-goals

- Solving general skill name stability with aliases, pin registries, or
  redirects. Index + BM25 is sufficient for the cases that remain after
  the two fixes above.
- Changing the patrol agent's behavior of creating new skill names
  during runs. That's a separate concern (patrol skill drift). Once the
  patrol's own checklist is no longer a skill, fewer such drift skills
  should be created in the first place; whatever remains can be
  consolidated freely.
- Touching the `LastUsedAt`-on-save fix from PR #275. That remains
  load-bearing for the 18-month staleness pruner regardless of this work.
- Making the dream aware of the live MCP server registry. The
  consolidation policy is declared by the convention's owner (the tool
  skill provider), not derived dynamically.

## Design

Two independent changes, neither dependent on the other.

### Part A — Scheduled task directives as first-class persistence

A scheduled task's directive has two parts today, with the split in the
wrong place:

- **Static framing** in `{taskName}.md` on disk in the agent profile
  (e.g. `heartbeat-patrol.md`). Loaded by `ScheduledTaskHandler.cs:51`.
  Ships with the repo, immutable at runtime. *Correctly placed.*
- **Evolving body** wedged into the skill store as
  `patrol/proactive-actions`. *Wrong placement.* The skill store is for
  reusable capabilities; this is task-specific instruction state.

The fix:

1. Add a per-task mutable directive field to the `ScheduledTask` record
   (or a sibling record keyed by task name in the same store). Persist
   via the existing `FileScheduledTaskStore`. This is per-task data,
   coupled in lifetime to the task — when the task is deleted, its
   evolving directive goes with it.

2. When a task fires, `ScheduledTaskHandler` composes the system prompt
   from: (a) the static `{taskName}.md` framing if present, plus (b) the
   evolving per-task directive body if present. No `get_skill` lookup is
   involved.

3. New tool: `update_task_directive(name, content)`. Replaces the
   current pattern of `save_skill("patrol/proactive-actions", ...)`.
   Operates on the scheduled-task store, not the skill store. Default
   authority: a running task can update its own directive. (Cross-task
   updates are out of scope; can be added later if needed.)

4. The existing `heartbeat-patrol.md` static framing is updated to point
   the agent at `update_task_directive` instead of
   `save_skill("patrol/proactive-actions", ...)`.

5. Migration: read the current content of `patrol/proactive-actions`
   from the skill store, write it to the new heartbeat-patrol task's
   evolving directive, delete the skill. One-shot startup migration in
   `HeartbeatBootstrapService` (idempotent — checks for the skill,
   migrates if found, no-ops otherwise).

After migration, `patrol/*` contains only true skills (drift artifacts
the patrol agent created during runs). They're now unprotected and the
next consolidation pass can clean them up.

### Part B — Convention-aware consolidation policy

`IToolSkillProvider` is the existing abstraction that owns naming
conventions for tool-related skills. Extend it with a consolidation
policy declaration.

```csharp
public enum ConsolidationPolicy
{
    /// <summary>
    /// Default. Skills under this prefix are topical variations and may
    /// be merged across, consolidated under a parent, or split as the
    /// dream judges useful.
    /// </summary>
    TopicalCluster,

    /// <summary>
    /// Each suffix under this prefix is a 1:1 binding to an external
    /// entity (MCP server, named script, web service). The dream may
    /// refine, split, or prune individual entries but must not merge
    /// across suffixes or extract a parent guide that replaces them.
    /// </summary>
    NamespacedSingleton,
}

public interface IToolSkillProvider
{
    // existing members...

    /// <summary>
    /// Optional. Declares consolidation policy for a name prefix this
    /// provider owns. Returns null for providers that don't own a
    /// prefix or don't care about policy (default = TopicalCluster).
    /// </summary>
    (string Prefix, ConsolidationPolicy Policy)? ConsolidationPolicy => null;
}
```

`McpToolSkillProvider` declares `("mcp/", NamespacedSingleton)`.
Plausibly `ScriptToolSkillProvider` declares `("scripts/",
NamespacedSingleton)` and any web-service provider declares its
prefix similarly. Each provider owns its own decision; the dream
consumes the aggregate.

`DreamService.BuildSkillConsolidationPromptAsync` (line 647) consumes
declared policies via DI:

- The prefix-cluster section (lines 686-703) filters out singleton
  prefixes entirely. The dream is never asked to consider parent
  extraction for them.
- An explicit constraints block is added to the user message:

  > *Skills under the following name prefixes are namespaced bindings
  > to external entities (e.g. MCP servers). For each such prefix, you
  > may refine an individual entry, split it, or remove ones that are
  > clearly stale, but you must not merge across suffixes within the
  > prefix and must not propose a single parent guide that replaces
  > them. Affected prefixes: `mcp/`, `scripts/`, ...*

- The deletion guards at `DreamService.cs:727` and `:1000` no longer
  consult `ProtectedSkillPrefixes` — they're removed.

The skill-consolidation directive (`agent/skill-dream.md`) is updated
to make the merged-summary keyword-preservation expectation explicit,
which is the implicit contract that keeps index + BM25 discovery
working across renames:

> *When merging skills, the resulting summary must preserve the
> search-relevant keywords from each original (tool names, service
> names, distinguishing terms users would search for) so the merged
> skill remains discoverable via BM25 recall on any query that would
> have surfaced an original.*

### Removal of `ProtectedSkillPrefixes`

After Parts A and B, no skill needs blanket prefix protection:

- `patrol/proactive-actions` is no longer a skill (Part A).
- `mcp/*` is protected from harmful consolidation by policy, not by
  delete-blocking (Part B).
- Other surfaces (hardcoded C# names, agent-created task descriptions,
  conventional naming patterns) self-correct via index + BM25 discovery
  and the agent's normal recreate-on-miss behavior.

`DreamOptions.ProtectedSkillPrefixes` is removed. The deletion guards
that consulted it are removed. The `LastUsedAt`-on-save fix from PR
\#275 stays.

## Migration

One-shot, in `HeartbeatBootstrapService`:

1. If skill `patrol/proactive-actions` exists, read its content.
2. Persist that content into the heartbeat-patrol task's new mutable
   directive field.
3. Delete the skill.
4. Log the migration. Future startups see no skill, no-op.

Drift skills under `patrol/*` are not migrated. They're released to the
next consolidation pass.

The MCP convention declaration is purely additive — the new
consolidation policy field is optional (default `TopicalCluster`),
existing providers keep working unchanged, and `McpToolSkillProvider`
declares its policy in the same PR that introduces the field.

## Open questions

- **Should agent-created scheduled tasks support per-task directives
  too?** `schedule_task` currently takes a description string. Could
  also accept an initial mutable directive that the agent fills in over
  successive runs (same shape as the heartbeat patrol pattern). Defer
  until there's a concrete second use case; the fix above is sufficient
  for the only existing scheduled task with this pattern.

- **Does `ConsolidationPolicy` belong on `IToolSkillProvider` or its
  own abstraction?** Putting it on `IToolSkillProvider` is cheapest and
  keeps the knowledge co-located with the tool guide that establishes
  the convention. If non-tool conventions emerge, split into
  `ISkillNamespacePolicyProvider`. Not worth doing speculatively.

- **What about skills the agent creates that fall under a singleton
  prefix the agent doesn't recognize?** Today only the
  `mcp/{server-name}` convention is declared via tool skill provider.
  If the agent invents `mcp/some-new-server` without going through the
  provider's flow, the dream still sees it as `mcp/*` and applies the
  declared policy. This is the correct outcome — the policy travels
  with the prefix, not with the creation path.
