---
title: Memory audit
layout: default
nav_order: 15
---

# Memory audit

A read-only, scheduled measurement of the agent's long-term memory store. It walks the memory
files on disk, compares them against the previous run, checks a set of invariants, writes a
plain-language report and a long-lived trend file, and speaks up only when something needs
attention.

## Why it exists

Everything the project knows about how memory management actually behaves came from a human
pulling the PVC and grepping Loki after the fact. That worked, but it does not scale and it does
not catch anything early:

- **Loki keeps about a week.** A corpus that loses 2% a month looks perfectly healthy in every
  seven-day window and is catastrophic across a year.
- **The dream-pass ledger records only last-run timestamps.** It cannot say how many
  consolidation cycles ran, and nothing anywhere records process restarts — which is what turned
  a day of deploys into a day of consolidation passes.
- **The dream cycle's own log lines looked healthy through every incident.** A pass that reports
  "12 merged, 8 archived" reports exactly that whether the merges were good or catastrophic.

The audit measures the store rather than trusting the code that writes to it, on its own
schedule, into a file whose retention is measured in months.

## What it measures

Each run appends one JSON row to `memory-audit/snapshots.jsonl` and rewrites
`memory-audit/latest.md`.

| Group | Fields |
| --- | --- |
| Size | `live`, `archived`, `malformedFiles`, `emptyCategoryDirs` |
| Movement since the previous run | `createdSinceLast`, `archivedSinceLast`, `hardDeletedSinceLast`, `purgedSinceLast`, `hardDeletedOutsidePurge`, `netGrowthPerDay`, `reinforcedWithoutMergeSinceLast` |
| Merge provenance | `mergeChainDepth` (histogram), `maxChainDepth`, `rejectedMergeSourcesSinceLast`, `rejectedMergeClustersRepeated` |
| Duplication | `nearDupPairs`, `nearDupEntries`, `embeddingDupClusters` |
| Shape | `reinforcement` (histogram), `topCategoriesByGrowth`, `vocabularyStoplistSize` |
| Cadence | `dreamPassesRunSinceLast`, `consolidationLastRunAt`, `restartsSinceLast` |
| Ahead | `purge` — how many archived entries are hard-deleted within the warning window, and how many of those the high-value floor will keep |
| Judgement | `invariants`, `status`, `eval` |

Two measurement choices are worth knowing about, because they are what make the numbers mean
what they say:

**Deltas are keyed on ids, never timestamps.** A merged entry inherits its earliest source's
`createdAt`, so counting creations by timestamp reports zero for exactly the churn this exists to
expose. The previous run's ids live in `memory-audit/state.json`.

**Hard deletes are explained, not assumed.** No purge timestamp is recorded anywhere in the
agent, so an id that vanished is attributed to the retention purge only when it was already
archived at the previous run *and* its `archivedAt + Dream:MemoryArchiveRetention` had passed.
Everything else lands in `hardDeletedOutsidePurge`, which the audit expects to be zero.

**Near-duplicates are lexical, not vector.** Word 6-gram shingles scored by Jaccard overlap. This
is deliberately not the mechanism consolidation uses: if the audit and the deduplicator both
asked the vector index "are these the same?", a broken index would read as a clean corpus.

At the default `ShingleSize` of 6 this measures *near-verbatim* duplication — the same text saved
twice, which is what a failing save-time dedupe looks like — rather than rephrasings, which share
few 6-grams. Lower `ShingleSize` if you want the looser measure, at the cost of pairing entries
that merely share boilerplate.

## Invariants

| Name | Severity | Meaning |
| --- | --- | --- |
| `no-hard-delete-outside-purge` | alert | Entries vanished from disk that the retention purge cannot account for. |
| `loss-percent-threshold` | alert | Live entries fell more than `MaxLossPercentBetweenSnapshots` since the previous run. |
| `merge-chain-unbroken` | alert | Entries archived `"merged into X"` where X exists nowhere — their content has no surviving copy. This is the shape issue #506 found. |
| `merged-from-resolves` | warning | A merge names sources that are missing and is too recent for the purge to explain it. Provenance dangling *after* the retention window is by design. |
| `archive-fields-present` | warning | An entry has an archive timestamp with no reason, or vice versa. |
| `live-not-merge-source` | warning | An entry that was merged away is still in recall, so both copies surface. |
| `no-repeated-rejection` | warning | The same merge cluster has been rejected on `RepeatedRejectionRuns` consecutive runs — consolidation is retrying work it cannot complete. |
| `net-growth-threshold` | warning | Saves are outpacing consolidation. |
| `chain-depth-threshold` | warning | A live entry is model prose generated from model prose, more deeply than you allowed. |
| `rejected-merges-threshold` | warning | Merge rejections per week are above the limit. |
| `no-malformed-files` | warning | Files under the memory root would not deserialize. |

Status is the worst finding: `alert` if any alert-severity invariant fired, `warning` if any
other did, `healthy` if none did.

## The weekly sample eval

The counters say what happened; they cannot say whether it was *right*. Once a week — by default
Sunday 05:00 — a Balanced-tier judge is shown a handful of decisions memory management actually
made and asked whether each was correct:

- **merges** made in the last 14 days, alongside the sources they replaced;
- **near-duplicate pairs** still both live — deduplication that did not happen;
- **heavily reinforced entries**, checked for having accreted into a vague blob;
- **facts dropped as ephemeral**, checked for having been durable after all.

The judge's directive lives at `/data/agent/memory-audit.md` on the profile volume, with a
built-in fallback. Results go to `memory-audit/eval-latest.json` and the summary is embedded in
every later snapshot row.

Cost is bounded two ways: sampling is capped per family, and the whole run is skipped when the
corpus fingerprint has not moved since the last eval. A quiet week costs nothing.

## Files

All under `{AgentProfile:BasePath}/memory-audit/` — `/data/agent/memory-audit/` in the Helm
chart.

| File | Contents |
| --- | --- |
| `snapshots.jsonl` | One snapshot per line. The trend. Kept for `SnapshotRetention` (400 days). |
| `latest.md` | The most recent report, in markdown. |
| `report-YYYY-MM-DD.md` | Dated copies, pruned by the dream cycle's shared file-age policy. |
| `eval-latest.json`, `eval-YYYY-MM-DD.json` | Sample-eval verdicts. |
| `state.json` | Private carry-over: the previous run's entry ids, rejection cluster counters, process starts. Not a public surface; losing it costs one run's deltas. |
| `consolidation-paused.json` | Present only when the circuit breaker has fired. |

When `CopyReportToShared` is on, the report is also written to
`/rockbot/shared/exports/memory-audit/memory-audit-YYYY-MM-DD.md`. That directory is re-created
on every run, because the shared-volume cleanup CronJob sweeps everything under `exports/` past
its TTL.

## How you find out

**The agent tells you.** A run whose status is not `healthy` publishes an unsolicited message on
the `scheduled-system` session with the status and the failed invariants. A healthy run is
silent — a channel that reports "all clear" daily stops being read before the day it matters. Set
`DigestCronSchedule` if you want the full report pushed on a schedule regardless of status.

**You ask.** Four MCP tools on the introspection sidecar read these files directly:

- `get_memory_audit` — the latest report plus the raw snapshot.
- `get_memory_audit_trend(days)` — snapshot rows for the last N days.
- `get_memory_audit_eval` — the latest judged sample.
- `resume_memory_consolidation` — clears the pause marker.

The agent's directives point memory-health questions at these rather than at `recall`: recall
searches what memory *contains*, the audit measures what the store is *doing* to it.

## Explaining a finding

Invariant names are stable identifiers, which makes them good keys and useless to a person.
Two surfaces translate them, and neither costs anything until it is used:

- **`get_memory_audit` returns a `findings` array** pairing each violation with its
  `MemoryAuditGlossary` entry — a plain-language title, what it means, what to do, and the
  severity. The explanation therefore arrives *with* the finding, so the agent can answer "what
  does `chain-depth-threshold` mean?" without a second call it might not think to make. Absent
  entirely on a clean run.
- **`get_tool_guide("memory-audit")`** returns the longer guide — how the measurement works, why
  memories are retired rather than deleted, what a merge chain is, and what the audit does not
  claim. Published through `IToolSkillProvider`, so it is loaded only when requested and is never
  injected into the system prompt.

`directives.md` carries only the routing rule (memory-health questions go to the audit, not
`recall`) plus a pointer to the guide. Everything else is deliberately kept out of the
always-loaded prompt.

A new invariant added without a glossary entry is a test failure, not a silent gap.

**Loki.** One structured `memory audit —` line per run carries every headline number, so a
dashboard can chart it without opening a file.

## The circuit breaker

`PauseConsolidationOnAlert` (off by default) makes a run that finds hard deletes outside the
purge, or a live-count drop past the threshold, write `consolidation-paused.json`. The dream
cycle checks for that file at the top of `RunMemoryConsolidationPassAsync` and skips the pass —
only that pass, so mining, extraction and the retention sweeps keep running.

The auditor never clears the marker. Resuming is deliberate: delete the file, or call
`resume_memory_consolidation`. The point of the pause is that a person looks at the cause before
the same pass runs again.

It is off by default because a false positive stops the only thing keeping the corpus from
growing without bound. Turn it on once you trust the numbers on your own deployment.

## Options

Section `MemoryAudit`. The Helm chart exposes `agent.memoryAudit.{enabled, cronSchedule,
evalCronSchedule, pauseConsolidationOnAlert}`; everything else is available through
`agent.extraEnv` as `MemoryAudit__*`.

| Option | Default | Notes |
| --- | --- | --- |
| `Enabled` | `true` | |
| `CronSchedule` | `0 4 * * *` | Kept clear of the 12-hourly dream cycle so the two do not queue on the agent's single work slot. |
| `InitialDelay` | `10m` | Long enough that a restart storm does not produce a snapshot per deploy. |
| `BasePath` | `memory-audit` | Relative paths resolve under `AgentProfile:BasePath`. |
| `SnapshotRetention` | `400d` | One row per day at a few hundred bytes is under 100 KB. |
| `NearDuplicateThreshold` | `0.3` | Much looser than save-time dedupe: this is a health metric, not a decision to fold anything. |
| `ShingleSize` | `6` | |
| `HighReinforcementFloor` | `20` | Eval sampling threshold. |
| `PurgeWarningDays` | `7` | Look-ahead for the purge outlook. |
| `MinRateWindow` | `12h` | Shortest gap between runs over which a per-day/per-week rate is measurable. Below it the rate reports as unmeasurable and the rate-based invariants are skipped — a restart otherwise annualizes a handful of saves into the thousands. |
| `MaxNetGrowthPerDay` | `5` | Only evaluated over windows of at least `MinRateWindow`. |
| `MaxMergeChainDepth` | `2` | |
| `MaxRejectedMergesPerWeek` | `5` | |
| `MaxHardDeletesOutsidePurge` | `0` | Any occurrence is an alert. |
| `MaxLossPercentBetweenSnapshots` | `10` | |
| `RepeatedRejectionRuns` | `3` | |
| `EvalEnabled` | `true` | |
| `EvalCronSchedule` | `0 5 * * 0` | |
| `EvalModelTier` | `Balanced` | |
| `EvalSampleSize` | `10` | Per family, per run. |
| `EvalWindow` | `14d` | |
| `EvalDirectivePath` | `memory-audit.md` | |
| `AlertOnAttention` | `true` | |
| `DigestCronSchedule` | `null` | Null means alerts only. |
| `CopyReportToShared` | `true` | |
| `SharedReportDirectory` | `/rockbot/shared/exports/memory-audit` | |
| `PauseConsolidationOnAlert` | `false` | |

## Notes and limitations

**The rejection stamp is written by the dream, not the auditor.** A refused merge previously
existed only as a log line. The dream now stamps each source with
`consolidationRejectedCluster` (a hash of the sorted source ids) and `consolidationRejectedAt`,
because only the dream knows which entries it proposed together — an auditor re-deriving the
clusters would be guessing at a decision an LLM already made. The stamps are metadata only:
nothing reads them back except the audit, and an entry is neither protected nor penalised by
carrying them.

**Category growth is attributed from surviving entries.** A hard-deleted entry takes its category
with it, so a corpus losing an entire category shows up in the hard-delete count rather than in
the growth table.

**`memory-audit.md` and `directives.md` reach a live cluster only via the PVC.** The init
container copies profile files with no-clobber semantics, so rebuilding the image does not update
them. Push them with `kubectl exec`; profile hot-reload picks the change up within ~500 ms.

**Rates need a window.** `netGrowthPerDay` is null, not zero, when two runs fall closer together
than `MinRateWindow` — which is what a restart does. Absolute counts in the same snapshot stay
exact, so a short-window run still catches real loss; only the extrapolated rates are withheld.

**Merge chain depth stops at a purged source.** Provenance is a recovery aid with a retention
window, and inflating a depth by guessing at entries that no longer exist would inflate exactly
the number an operator would act on.

## Related

- [Dream service](dream-service) — the passes the audit is measuring.
- [Memory](memory) — the store itself.
