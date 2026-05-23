# Worker subagents — patrol migration measurements

Wall-clock and tool-call observations captured before and after the phase 3
live migration of the `heartbeat-patrol` directive to the worker-aware
checklist (`deploy/agent-seeds/heartbeat-patrol.checklist.md`). Used as input
for phase 4 tightening.

Sampling caveat: single fires are noisy. Where a number looks borderline,
capture two more fires before drawing conclusions.

---

## heartbeat-patrol — pre-migration (worker-free)

- **Date:** 2026-05-21
- **Fire start:** 2026-05-21 17:20:00 UTC (cron `0 */6 * * *`, originally
  scheduled 17:00 UTC; preempted by active user session, retried 10× over
  20 min before firing)
- **Agent image:** `rockylhotka/rockbot-agent:0.11.3` (phase 1 not deployed —
  `spawn_workers` tool was not registered; patrol used `spawn_subagent`)
- **Directive on PVC:** legacy 3887-char checklist (Steps 1–9, gather inline
  or via `spawn_subagent`)

### Wall-clock

| Phase | Time (UTC) | Notes |
|---|---|---|
| Primary fired | 17:20:00 | 3 × `spawn_subagent` issued in parallel |
| Scheduler-health subagent done | 17:24:11 | ~4 min |
| Active-plans subagent done | 17:27:17 | ~7 min |
| Calendar+email subagent done | 17:29:12 | ~9 min — slowest, drove batch close |
| Consolidated synthesis ran | 17:29:12 | post-batch combine |
| Last patrol tool call | 17:30:04 | stale-entry cleanup |
| **Total wall-clock** | **~10 min** | |

### LLM turns and tokens

Hard to derive precisely from agent logs; rough proxy from per-subagent
progress entries and tool-call markers (one log line per tool round-trip):

- Primary patrol agent: ~6 tool calls (3 × `spawn_subagent`, 3 × `SaveToWorkingMemory`)
  then waited for batch
- Calendar+email subagent: ~12+ tool calls (service-details lookup, account
  list, parallel calendar/email fetch via `spawn_wisps`, batch artefact reads,
  shared write)
- Active-plans subagent: ~8 tool calls (memory search, todo list, shared
  memory reads, shared write)
- Scheduler-health subagent: ~6 tool calls (service registry inspection,
  shared memory reads, shared write)
- Post-batch synthesis on primary: ~4 tool calls

Total: ~36 LLM-driven tool rounds across primary + 3 full subagents.

Token usage not reliably extractable from logs at this checkpoint —
left blank until telemetry surfaces per-fire totals.

### Notes

- Three full subagents were spawned (one each for calendar+email,
  active-plans, scheduler-health), each carrying full long-term-memory /
  identity / KG injection. The schema cost per subagent is significant.
- Each subagent independently re-resolved tool schemas before invoking
  `calendar-mcp` — duplicate work.
- The calendar+email subagent's initial wisp summary "collapsed outputs",
  forcing it to re-fetch raw records — added one full LLM round.

---

## heartbeat-patrol — post-migration (worker-aware)

- **Date:** _to be filled after the next natural patrol fire (cron `0 */6 * * *`)_
- **Agent image:** `rockylhotka/rockbot-agent:0.12.0` (phase 1 deployed)
- **Directive on PVC:** worker-aware checklist
  (`deploy/agent-seeds/heartbeat-patrol.checklist.md`, 6048 chars)

### Wall-clock

| Phase | Time | Notes |
|---|---|---|
| Primary fired | _TBD_ | |
| Prep `search_memory` for plans | _TBD_ | one direct primary tool call |
| `spawn_workers` batch issued | _TBD_ | 3 worker definitions, parallel |
| Workers done | _TBD_ | batch closes on slowest |
| Synthesis + act + promote walk | _TBD_ | |
| **Total wall-clock** | _TBD_ | |

### LLM turns and tokens

_to be filled — expectation: sharp drop in turn count because each gather
slice runs on a lean worker loop (no LTM/episodic/KG injection, low-tier
model, tight iteration cap) rather than a full subagent._

### Notes

_to be filled — capture worker `convergedPatterns` activity, any blocked
workers (tool not in allowlist, schema failures), and whether the primary
ended up paraphrasing the directive or executed it verbatim._

---

## Decision gates for phase 4

After 2–3 post-migration patrol fires, revisit:

- **Worker timeout default.** Current 5 min per worker. Reset from observed
  P95 wall-clock.
- **`MaxIterations` ceiling.** If workers consistently complete in fewer
  iterations, tighten the cap.
- **`tools_allow` shape.** The phase 3 seed deliberately omits `tools_allow`
  because the registry holds management proxies (`mcp_invoke_tool`) rather
  than per-server prefixes. Once we know which registry tools each worker
  actually invokes, narrow each definition's allowlist by exact name to
  shrink schema injection cost. See the "Notes for phase-4 tightening"
  section in the seed file.
- **Low-tier sufficiency.** If a category of workers consistently fails on
  Low-tier, promote the default tier ceiling. The trigger condition needs
  definition once we have failure-class data.
