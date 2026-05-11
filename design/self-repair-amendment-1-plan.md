# Self-Repair Amendment 1 — Implementation Plan

Companion to `design/self-repair.md` Amendment 1 ("Surface, don't substitute").
Each step below is one commit. The first commit also includes the design amendment.

## Step 1 — Remove fan-out (commit also includes the amendment)

**Goal:** stop the `get_email_details` and `send_email` failure cascades immediately.
This is a net improvement on its own: less broken behaviour, nothing new to validate.

**Files to change**

- `src/RockBot.Tools.Mcp/Recovery/Providers/AccountIdFanoutProvider.cs` — **delete**.
- `src/RockBot.Tools.Mcp/Recovery/McpRecoveryExecutor.cs`:
  - Delete the fan-out branch in `RetryAsync` (the `resolved.RequiresFanOut` arm).
  - Delete `FanOutAsync`, `AggregateFanOut`.
  - Replace the `(ToolInvokeResponse, Dictionary<string,object?>?)` tuple return with `ToolInvokeResponse` + new merged args computed inline.
- `src/RockBot.Tools.Mcp/Recovery/ResolvedDefault.cs` — remove `RequiresFanOut` property (keep the record).
- `src/RockBot.Tools.Mcp/Recovery/IToolArgumentDefaultsProvider.cs` — doc-comment cleanup; contract is now "single value or null".
- `src/RockBot.Tools.Mcp/McpServiceCollectionExtensions.cs` — drop the `AccountIdFanoutProvider` registration (line 58).

**Tests**

- Delete `tests/RockBot.Tools.Tests/Recovery/AccountIdFanoutProviderTests.cs`.
- `tests/RockBot.Tools.Tests/Recovery/McpRecoveryExecutorTests.cs`:
  - Delete fan-out scenarios.
  - Keep "missing field → provider fills → success" path for environmental defaults (e.g. `TimeZoneDefaultProvider`).
  - Add: "provider claims to fill but the call still fails with a *different* missing field → response surfaces both errors, no chained recovery via fan-out, no capability claim records the resolved-field as the failure."
- `tests/RockBot.Tools.Tests/Recovery/McpRecoveryExecutorCapabilityClaimTests.cs`:
  - Remove the test that expects `"AccountIdFanoutProvider resolved field accountId but the call still failed"`-shape claims (these go away).

**Acceptance**

- `dotnet build RockBot.slnx` passes with no `AccountIdFanoutProvider` references.
- `dotnet test RockBot.slnx` passes.
- `grep -ri AccountIdFanout src/ tests/` returns zero hits.
- A unit test reproducing the `get_email_details` shape (no args → "accountId is required") returns the single error to the caller — no fan-out, no `recovery-exhausted` capability claim written.

**Risk**

Low. The only consumers of fan-out semantics were the recovery executor and its tests.
Production behaviour for `get_calendar_events` shifts from "scan all calendars
automatically" to "return `accountId is required` to the LLM". The LLM will need to
either supply an accountId or spawn N wisps — that's the wisp-orchestration story
Step 2 makes legible.

---

## Step 2 — Schema-error enrichment

**Goal:** when a tool call returns a missing-required-field error and no environmental
default fills it, return an enriched error to the LLM containing the field schema, any
recent same-session results that name the same field, and any tool-description hint.
The LLM threads the value on retry and is expected to save a skill.

**Files to add**

- `src/RockBot.Tools.Mcp/Recovery/SchemaErrorEnricher.cs` — new class. Inputs: `serverName`, `toolName`, `fieldName`, `sessionId`, `originalError`. Outputs: a formatted error string for `ToolInvokeResponse.Content`. Composes:
  - Field schema from `mcp_get_service_details` (cached per `(server, tool)` for the process lifetime — schemas don't change without a reconnect).
  - Recent in-session tool calls via `IToolCallLog.GetBySessionAsync`, filtered to calls returning JSON whose values include keys matching `fieldName` (case-insensitive). Show the most recent 3 matches with their `toolName` and the matching key path (e.g. `search_emails → result[2].id = "abc..."`).
  - The first sentence of the tool description if it mentions the field name (substring match).
- `src/RockBot.Tools.Mcp/Recovery/ToolSchemaCache.cs` — small per-process cache keyed by `(server, tool)`, lazily populated.

**Files to change**

- `src/RockBot.Tools.Mcp/Recovery/McpRecoveryExecutor.cs`:
  - When Stage A providers return null (no environmental default applies), call `SchemaErrorEnricher.EnrichAsync(...)` and replace the response `Content` with the enriched text. Mark `IsError=true`. Do **not** write a capability claim.
  - Still record the failure into `IFailureClusterStore` (the cluster threshold becomes a backstop for "LLM keeps making the same mistake despite enrichment").
- `src/RockBot.Tools.Mcp/McpServiceCollectionExtensions.cs` — register `SchemaErrorEnricher` and `ToolSchemaCache`.

**Tests**

- `tests/RockBot.Tools.Tests/Recovery/SchemaErrorEnricherTests.cs` — new:
  - Returns schema lookup when available.
  - Includes recent session matches when `IToolCallLog` has them.
  - Excludes matches from other sessions.
  - Tolerates missing schema (no `mcp_get_service_details` cache entry) — returns the original error plus session matches only.
  - Tolerates empty session log.
- `tests/RockBot.Tools.Tests/Recovery/McpRecoveryExecutorTests.cs` — extend:
  - "Missing field with no environmental default → response contains enriched error text and a session-recent match."
  - "Missing field with environmental default → still fills silently (TimeZoneDefaultProvider unchanged)."

**Acceptance**

- A call to `calendar-mcp/get_email_details` with no args, in a session whose log contains a prior successful `search_emails`, returns a single error whose content names: `accountId` and `emailId` schemas, the prior `search_emails` items with their `id` and `accountId` fields, and the tool-description pointer if present.
- A call on a brand-new MCP server with no provider config completes within 2 LLM turns for any missing-required-field error in the integration test suite.

**Risk**

Medium. New code path. The schema cache must handle MCP server disconnect/reconnect
(invalidate on `McpServersIndexed`). The session-match scan must be bounded — cap at
the last N tool calls or last M minutes.

---

## Step 3 — Remove Stage B

**Goal:** delete `StageBLlmFiller` and its wiring. Enriched errors replace it.

**Files to change**

- `src/RockBot.Tools.Mcp/Recovery/StageBLlmFiller.cs` — **delete**.
- `src/RockBot.Tools.Mcp/Recovery/McpRecoveryExecutor.cs` — remove the Stage B branch (lines ~204–264) and the constructor parameter.
- `src/RockBot.Tools.Mcp/McpServiceCollectionExtensions.cs` — drop the `StageBLlmFiller` registration (line 63).

**Tests**

- Delete `tests/RockBot.Tools.Tests/Recovery/StageBLlmFillerTests.cs`.
- `tests/RockBot.Tools.Tests/Recovery/McpRecoveryExecutorTests.cs` — remove Stage B scenarios.

**Acceptance**

- `grep -ri StageBLlmFiller src/ tests/` returns zero hits.
- Tests pass.
- A failure that previously triggered Stage B now produces an enriched error (Step 2 path).

**Risk**

Low. Stage B was used only when Stage A had nothing; both routes now converge on
enrichment.

---

## Step 4 — Verify capability-claim-shaped observations

**Goal:** kill legacy "wrapper cannot pass arguments" rationalisations on their next
injection, without requiring an explicit `VerifyShape`.

**Files to change**

- `src/RockBot.Host/AgentContextBuilder.cs` (or wherever WM injection happens — likely a helper used during context assembly):
  - For working-memory entries tagged `kind=observation` whose content trips `ObservationLanguageDetector.LooksLikeCapabilityClaim`, run a lightweight verification: extract any `(server, tool)` pairs named in the entry text via a small regex on `\b([a-z][a-z0-9-]*?)/([a-z_][a-z_0-9]*)\b`; query `IToolCallLog` for a successful call to any of those pairs within the last N hours (cluster-store retention window). If found, evict the entry and skip injection.
  - Cache the eviction decision in-process for the session so we don't re-scan per turn.
- `src/RockBot.Memory/ObservationLanguageDetector.cs` — add a helper `TryExtractToolReferences(string content): IReadOnlyList<(string Server, string Tool)>`.

**Tests**

- `tests/RockBot.Memory.Tests/ObservationLanguageDetectorTests.cs` (create or extend): tool-reference extraction.
- `tests/RockBot.Host.Tests/AgentContextBuilderObservationEvictTests.cs` — new:
  - Observation naming `calendar-mcp/search_emails` is evicted when log shows a recent success.
  - Observation with no tool reference is injected as before.
  - Observation whose tool has only failures in the log is injected.

**Acceptance**

- Manually-seeded WM entry "Fresh email search was blocked because the wrapper cannot pass `search_emails` parameters" is evicted on the next context build for any session whose tool-call log contains a successful `calendar-mcp/search_emails`.
- Entries that are *not* capability-claim-shaped pass through unchanged.

**Risk**

Low-medium. False eviction is the main concern — an observation about a tool that
*sometimes* works could be evicted even when it's reporting a real new failure. Mitigation: require the recent success to be within a much shorter window than the
observation's age (e.g. success must be newer than the observation), so genuine new
failures aren't masked by stale success.

---

## Step 5 — Cleanup and contract narrowing

**Goal:** remove dead surface area now that fan-out and Stage B are gone.

**Files to change**

- `src/RockBot.Tools.Mcp/Recovery/IToolArgumentDefaultsProvider.cs` — narrow the contract doc-comment to "environmental defaults only".
- `src/RockBot.Tools.Mcp/Recovery/ResolvedDefault.cs` — if `RequiresFanOut` wasn't already removed in Step 1, remove now. (Likely already done.)
- `design/self-repair.md` — Phase 1 section: add a forward pointer to Amendment 1 ("Stage A/B in this section are superseded — see Amendment 1 for the current contract").
- Cross-check `src/RockBot.Tools.Mcp/Recovery/Providers/FileToolDefaultsProvider.cs` (Phase 4 file-backed provider): confirm it never registers fan-out defaults; tighten its parser to reject any config that would have implied fan-out.

**Tests**

- Touch-ups only; no new functional coverage.

**Acceptance**

- `dotnet build` + `dotnet test` clean.
- `grep -ri RequiresFanOut src/ tests/` returns zero hits.

**Risk**

Trivial.

---

## Commit & PR plan

- Branch: `rockfordlhotka/self-repair-surface-not-substitute` (already created).
- Five commits, one per step. **Commit 1 includes the `design/self-repair.md` amendment.**
- Open the PR after Step 3 (recovery contract is internally coherent again). Land Steps 4–5 as follow-up commits on the same PR.
- PR title: `Self-repair: surface schema errors instead of substituting values`.
- PR body: short summary + link to Amendment 1 section anchor.

## Verification pass before merge

1. Deploy the agent image to the cluster.
2. Watch the failure-cluster JSONL for `get_email_details|emailId` over one heartbeat-patrol cycle. Cluster should grow at most once (first turn), then stop.
3. Inspect the next `shared/pending/email-triage-latest` WM write — it should no longer contain the "wrapper cannot pass arguments" rationalisation.
4. Confirm no `recovery-exhausted` capability-claim files land under `/data/agent/memory/claim/capability/`.

## Out of scope for this branch

- Tightening subagent prompts that currently include "wrapper-blocked" fallback language (`heartbeat-patrol.md` etc.). Worth a separate PR once the underlying mechanism is fixed and we know what guidance is still needed.
- Phase 5 threshold changes — leave as-is. Should be unnecessary once enrichment lands; if not, revisit.
- Skill auto-edit churn improvements (Phase 4 risk in the original design).
