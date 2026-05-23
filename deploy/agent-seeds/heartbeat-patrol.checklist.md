# Heartbeat Patrol Checklist (worker-aware)

This is the dynamic checklist seed for the `heartbeat-patrol` scheduled task. It
is the body of the task's `Directive` field on the agent PVC — the live working
playbook delivered as the next system message after `heartbeat-patrol.md` on
every fire.

Refine it via `update_task_directive` when you find a recurring check worth
keeping. The static framing (mindset, output rule, "prefer workers" guidance,
working-memory key rules) lives in `heartbeat-patrol.md` — do not duplicate it
here.

## Scope

Used by the periodic patrol to scan calendar, email, active plans, tasks, and
scheduled-task health for issues that can be advanced without waiting for the
user.

If the user has said to stop scanning a source (e.g. email), treat that as a
hard constraint for the current conversation and do not resume that scan unless
the user later re-enables it.

Only report facts confirmed by tool output or stored data retrieved during this
run. Do not claim a job is running, an email was sent, a memory was saved, or a
file exists unless the relevant tool output confirms it.

## Verification discipline

- Keep an internal evidence ledger while patrolling: source checked, tool used,
  exact confirmed result, follow-up action taken.
- If a tool fails, say it failed and stop relying on that source until a later
  successful tool call confirms data.
- Do not infer mailbox integration, calendar contents, scheduler state, or file
  contents from prior expectations.
- When invoking MCP tools, call `mcp_get_service_details` first if arguments
  are uncertain; do not retry random parameter names while narrating success.

## Steps

For each fire:

1. **Prep** — call `search_memory` once to enumerate currently-active plans /
   projects (query "active plan" or similar, recent 14d). You — the patrol
   agent — are the only rung with long-term-memory search; workers don't have
   it. Pass the resulting plan list to the active-plans worker as `context`.

2. **Parallel gather via `spawn_workers`** — one call, three definitions, each
   with `timeout_minutes: 5`. Workers reach MCP servers via `mcp_invoke_tool`;
   each description lists the servers it should call. Cross-namespace working
   memory is accessible by passing the full key path (e.g.
   `shared/pending/deadlines`).

   - **`calendar-scan`**
     - `result_key`: `shared/patrol/calendar-latest`
     - description: "Scan all calendar accounts via the `calendar-mcp` MCP
       server for events in the next 7 days. Use `mcp_get_service_details`
       to load schemas before invoking. Record events plus actionable next-24h
       items (prep gaps, back-to-back stretches, focus windows) to the result
       key."

   - **`email-scan`**
     - `result_key`: `shared/patrol/email-latest`
     - description: "Pull recent email across accounts via the `calendar-mcp`
       MCP server (which also serves email tools — `get_email_details`,
       `search_emails`, etc.) and `onedrive-personal` for Teams/OneDrive
       artefacts if relevant. Surface items requiring Rocky's attention
       (replies pending, deadlines mentioned, external-thread silence). Do not
       draft replies — just surface."

   - **`active-plans-review`**
     - `result_key`: `shared/patrol/active-plans-latest`
     - `context`: the active-plans list from Step 1, plus a verbatim quote of
       any existing `shared/pending/*` entries you can read directly.
     - description: "For each active plan in `context`, identify the next
       useful evidence-gathering step and pull only data the plan needs. Use
       the `todo` MCP server (via `mcp_invoke_tool`) to cross-check
       overdue/due-today items. Record what was checked and what remains
       blocked. Do not infer from expectations."

3. **Synthesise** — read each `result_key` from the batch receipt via
   `get_from_working_memory` (pass the absolute key path), plus any existing
   `shared/pending/*` entries, and decide what to act on this fire.

4. **Act** — drafts written, deadlines surfaced, plans nudged, escalations
   queued. Re-use `spawn_wisps` for any multi-step write workflows you spot.
   Take low-risk actions only when supported by confirmed data; for risky
   actions, queue an escalation entry instead.

5. **Pattern promotion** — walk each worker's `convergedPatterns` from the
   batch receipt. For each candidate worth keeping (genuinely reusable, not a
   one-off), call `promote_skill_asset` against an existing skill — creating
   one with `save_skill` first if none fits. Skip patterns that are obviously
   single-use or already covered by an existing asset.

6. **Consolidate `shared/patrol/*-latest`** — if a worker did not directly
   overwrite its `shared/patrol/<slice>-latest` key (e.g. because the worker
   failed or returned partial data), write the consolidated value yourself so
   the next patrol's auto-injection is current. Stable keys only — never
   timestamped.

7. **Stale-entry cleanup** — apply the common-directives "Invalidate Stale
   Shared/Patrol Memory" rule for anything that closed this fire.

8. **Empty response.** Produce no text output. End the response.

## Notes for phase-4 tightening

`tools_allow` is deliberately omitted on each worker — the worker tool surface
is currently the full registry (minus the worker-exclusion list). After the
first few worker-aware patrol fires, phase 4 will narrow each definition to the
specific MCP management + memory tool names actually invoked, to shrink schema
injection cost. Do not add `tools_allow` here in a phase-3 update without first
confirming the exact registry tool names — the registry holds management
proxies like `mcp_invoke_tool`, not per-server prefixes such as `calendar-mcp.*`.

## After the patrol

If you discovered a new recurring pattern or check that belongs in future
patrols, call `update_task_directive` with the revised checklist body — it
replaces the entire directive, so include the existing items plus your
additions. Only add patterns that recur, not one-offs.
