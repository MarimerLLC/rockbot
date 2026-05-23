# WorkIQ Phase 5 — Re-consent UX and failure-mode polish

Tracking issue: [#441](https://github.com/MarimerLLC/rockbot/issues/441). Builds on
[#442](https://github.com/MarimerLLC/rockbot/pull/442) (Phase 1+2),
[#443](https://github.com/MarimerLLC/rockbot/pull/443) (Phase 3), and the Phase 4
operational plan in [`workiq-phase4-plan.md`](workiq-phase4-plan.md).

## Goal

After Phase 4 lands and Work IQ tools work end-to-end, polish the failure
modes so a token expiry feels like a clean recoverable event rather than a
mystery. Three concrete deliverables:

1. **Surface `MsalUiRequiredException` as an actionable `auth_required` tool
   error.** Today it falls into the generic "execution failed, retryable"
   bucket, so the LLM blindly retries instead of telling the user to reconnect.
2. **Decide and implement scheduled-task behavior under expired auth.** Choose
   between fail-fast, skip-task, or hide-tools approaches. Mark the answer in
   one place so future Work IQ-adjacent tools follow the same convention.
3. **Operational documentation** — re-consent troubleshooting in
   `deploy/workiq-setup.md` (the runbook produced by Phase 4) so the first
   time a user hits expiry they know what to expect.

## What is and is not in this phase

| In | Out |
|---|---|
| `TokenAcquisitionException` → `ToolError` path with `auth_required` code and actionable message | Multi-user re-consent flows |
| Bus-level coordination so scheduled tasks know to skip Work IQ when auth is expired | New per-tool auth UIs |
| Reconnect-button idempotency in Blazor (double-click guard) | Auth state persisted across agent pod restarts beyond what the cache file already gives us |
| Observability metric / log line summarizing WorkIQ auth health | Telemetry dashboard work — leave to the operator |
| Phase 4 runbook addendum for re-consent flow | Replacing Phase 3's banner with a different UI pattern |

## Background: the gap we're closing

Today's exception flow when MSAL silent refresh fails:

```
MsalTokenProvider.GetAccessTokenAsync
  catch (MsalUiRequiredException) →
      publish WorkIqAuthExpired         ✓ (UI banner appears)
      throw TokenAcquisitionException(ReauthRequired)

BearerInjectionHandler.ApplyBearerAsync
  → TokenAcquisitionException bubbles up unchanged

McpBridgeService.HandleToolInvokeAsync
  catch (Exception ex) when FindAuthChallenge(ex) is not null  ← MISSES
      (FindAuthChallenge only walks for McpAuthChallengeException)
  catch (Exception ex)
      → reconnect-and-retry path (won't help; token is the problem)
      → ToolError { Code=ExecutionFailed, IsRetryable=true }
```

The LLM sees `execution_failed, retryable` and dutifully retries. The retry
also fails. The agent eventually surfaces a generic "execution_failed" to the
user. Meanwhile the Blazor banner *is* already up — but the chat reply
doesn't reference it.

We want:

```
McpBridgeService.HandleToolInvokeAsync
  catch (Exception ex) when FindReauthRequired(ex) is not null
      → ToolError {
          Code = "auth_required",
          Message = "Microsoft 365 connection has expired. Open the Blazor app
                     and click 'Reconnect M365' to restore access. Work IQ tools
                     will fail until reconnection is complete.",
          IsRetryable = false
        }
```

So the LLM stops retrying, returns a clear message to the user, and the chat
reply reinforces the banner that's already on screen.

## Deliverables

### 1. ToolError mapping for `MsalUiRequiredException` upstream

**Files touched:**

- `src/RockBot.Agent/McpBridge/McpBridgeService.cs` — add a second walker
  helper alongside `FindAuthChallenge`:
  ```csharp
  private static TokenAcquisitionException? FindReauthRequired(Exception? ex)
  {
      for (var cur = ex; cur is not null; cur = cur.InnerException)
          if (cur is TokenAcquisitionException tae
              && tae.Code == TokenAcquisitionException.Codes.ReauthRequired)
              return tae;
      return null;
  }
  ```
  Add a new catch clause **before** the `FindAuthChallenge` one (order matters
  because reauth-required is more specific than a generic 401 challenge):
  ```csharp
  catch (Exception ex) when (FindReauthRequired(ex) is { } reauth)
  {
      sw.Stop();
      var error = new ToolError {
          ToolCallId = request.ToolCallId,
          ToolName = request.ToolName,
          Code = ToolError.Codes.AuthRequired,
          Message = "Microsoft 365 connection has expired. Open the Blazor "
                    + "app and click 'Reconnect M365' to restore access. "
                    + "Work IQ tools will fail until reconnection is complete.",
          IsRetryable = false
      };
      await PublishResponseAsync(error, replyTo, envelope.CorrelationId, ct);
      return MessageResult.Ack;
  }
  ```
- Also handle `NotAuthenticated` (initial consent never completed) — different
  message, same `auth_required` code, also non-retryable. Same walker pattern.

**Tests:**

- `tests/RockBot.Agent.Tests/McpBridge/Auth/` — add `MsalToolErrorMappingTests`
  that drives the bridge service end-to-end with a stub `ITokenProvider` that
  throws `TokenAcquisitionException(ReauthRequired)` and asserts the resulting
  `ToolError` has the expected code and message. Requires extracting a small
  test seam in `McpBridgeService` (or testing through a real bridge with a
  registered fake server — the test harness pattern from Phase 2 can be
  extended).

### 2. Scheduled-task behavior under expired auth

**The decision to make** — what happens when a patrol or scheduled task
attempts a Work IQ tool while auth is expired?

Three options, with my recommendation flagged:

| Option | Behavior | Pros | Cons |
|---|---|---|---|
| **Fail-fast** | Tool call returns `auth_required`; patrol's reasoning sees it and either skips that step or reports failure in its summary. | Simplest; no new state to track. | Every patrol cycle generates noise in logs / notifications until the user reconnects. |
| **Hide tools** *(recommended)* | When the agent knows WorkIQ is unauthenticated, the `workiq-*` tools are filtered out of the agent's tool list at session-start time. The LLM never sees them; never tries to call them. | Patrols stop generating noise; the LLM's reasoning naturally adapts. | Requires an in-process flag on the agent side (`IsWorkIqHealthy`) maintained by listening to `WorkIqAuthCacheUpdated` (set healthy) and the agent's own auth_required errors (set unhealthy). |
| **Skip-task-class** | Patrol metadata declares which Work IQ servers it needs; the scheduled-task handler skips the whole patrol when any required server is unauthenticated. | Surgical — only patrols that need WorkIQ are affected. | Requires patrol authors to declare dependencies in metadata, which is a new convention. |

**Recommended: Hide tools.** Justification:

- The mechanism (filter tools by health) generalizes to any future
  bearer-auth MCP server, not just WorkIQ.
- It's invisible to patrol authors — they write patrols as if all tools
  exist, and the framework silently degrades.
- The user-visible signal (the Blazor banner) is unchanged regardless of
  what the agent is internally doing.
- It still surfaces an `auth_required` error if something *does* attempt
  a Work IQ tool (the bridge layer keeps its catch from deliverable #1),
  so a directly-typed tool call still produces a useful error.

**Files touched:**

- `src/RockBot.Agent/McpBridge/Auth/WorkIqHealthTracker.cs` — new singleton.
  Subscribes to `WorkIqAuthCacheUpdated` (sets `IsHealthy = true`); exposes a
  `MarkUnhealthy(string reason)` method called by `MsalTokenProvider` after
  publishing `WorkIqAuthExpired`. Optionally exposes a `HealthChanged`
  event so other components (tool registry filter) can react.
- `src/RockBot.Agent/McpBridge/McpBridgeService.cs` — when building the tool
  list to publish on `tool.meta.mcp.{agentName}`, filter out any server whose
  `Auth.Profile == "workiq"` when `WorkIqHealthTracker.IsHealthy == false`.
  Re-publish the tool list when health flips.
- `src/RockBot.UserProxy.WorkIqAuth/WorkIqAuthMessages.cs` — possibly add a
  `WorkIqAuthRestored` message so the agent's health tracker can flip back
  to healthy without waiting for the next cache-updated round-trip. Actually,
  `WorkIqAuthCacheUpdated` already serves this role — the agent assumes
  health on receipt. No new message type needed.

**Tests:**

- `WorkIqHealthTrackerTests` — verifies state transitions on
  `WorkIqAuthCacheUpdated` arrivals, on `MarkUnhealthy` calls, and that the
  `HealthChanged` event fires.
- Extend `McpServersIndexedHandlerTests` (or wherever the tool list publish
  logic is) to confirm that tools from auth-profile servers are filtered when
  the tracker is unhealthy.

### 3. Reconnect-button idempotency in Blazor

**The problem:** the `WorkIqConnect` razor component's "Connect M365" button
calls `WorkIqDeviceCodeFlow.BeginAsync`. If the user double-clicks, two MSAL
device-code flows kick off in parallel. Both complete (one succeeds, the
other fails with `verification_code_expired`), the latter's failure trashes
the success state.

**Fix:**

- `src/RockBot.UserProxy.Blazor/Components/WorkIqConnect.razor` — disable the
  button when `_state == ConnectState.InProgress`, and use a `bool _starting`
  guard during the `BeginAsync` call to handle the race where two clicks
  fire before the first one transitions to `InProgress`.
- Same guard in `WorkIqReconnectBanner`'s Reconnect button (it just opens
  the modal but should also be guarded against double-fire).

**Tests:**

- Extend `WorkIqAuthUiServiceTests` with `WorkIqConnectGuardTests` — test
  that a second call to BeginAsync while the first is pending is a no-op
  (returns the existing challenge). May require a small async coordinator on
  `WorkIqDeviceCodeFlow` to support this; alternative is to keep the guard
  purely in the UI component.

### 4. Observability — log + metric for WorkIQ auth health

**Files touched:**

- `WorkIqHealthTracker` (from deliverable #2) gets an `ILogger` and writes
  a structured log line on every state change:
  ```
  WorkIQ auth health changed: Healthy=true → false. Reason: refresh_revoked.
  ```
- If the agent's telemetry pipeline supports custom metrics (check
  `RockBot.Telemetry` for the right hook), emit `workiq.auth.healthy`
  gauge (0/1). Otherwise the log line is enough — operators can filter
  on it.

**Tests:** the log assertion lives in `WorkIqHealthTrackerTests` (use a
test logger that captures messages).

### 5. Documentation

**Files touched:**

- `deploy/workiq-setup.md` (created by Phase 4) — add a new section,
  "Re-consent flow":
  - What the user sees when expiry happens (banner, chat error message).
  - Exact click sequence to recover (M365 pill → Reconnect → device code →
    sign in → confirmation).
  - How long it takes (~30 seconds end-to-end if the user is fast).
  - What the agent does during expiry (Work IQ tools hidden from LLM
    reasoning; other tools unaffected).
  - How operators verify recovery (`workiq.auth.healthy` log line or
    direct cache file mtime check).
- `deploy/workiq-setup.md` — add a "Scheduled tasks during expiry" subsection
  documenting the hide-tools behavior so operators understand why patrols
  stop calling WorkIQ until reconnect.

No changes to `deploy/workiq-entra-app-registration.md` — that doc is
shareable with IT and doesn't need Phase 5 detail.

## Files / artifacts produced by this phase

| Path | Status | Purpose |
|---|---|---|
| `src/RockBot.Agent/McpBridge/McpBridgeService.cs` | edited | New catch + walker for `TokenAcquisitionException(ReauthRequired)` and `NotAuthenticated` |
| `src/RockBot.Agent/McpBridge/Auth/WorkIqHealthTracker.cs` | new | In-process auth-health flag + log emission |
| `src/RockBot.Agent/McpBridge/Auth/MsalTokenProvider.cs` | edited | Call `WorkIqHealthTracker.MarkUnhealthy` alongside `WorkIqAuthExpired` publish |
| `src/RockBot.Agent/McpBridge/Auth/TokenCacheStore.cs` | edited | Call `WorkIqHealthTracker.MarkHealthy` after successful cache write |
| `src/RockBot.Agent/McpBridge/Auth/WorkIqAuthServiceCollectionExtensions.cs` | edited | Register `WorkIqHealthTracker` as singleton |
| `src/RockBot.UserProxy.Blazor/Components/WorkIqConnect.razor` | edited | Double-click guard |
| `src/RockBot.UserProxy.Blazor/Components/WorkIqReconnectBanner.razor` | edited | Same |
| `tests/RockBot.Agent.Tests/McpBridge/Auth/MsalToolErrorMappingTests.cs` | new | Asserts ToolError shape |
| `tests/RockBot.Agent.Tests/McpBridge/Auth/WorkIqHealthTrackerTests.cs` | new | State transitions + event firing |
| `tests/RockBot.UserProxy.Blazor.Tests/WorkIqConnectGuardTests.cs` | new | Idempotency |
| `deploy/workiq-setup.md` | edited | Re-consent flow + scheduled-task behavior sections |

## Risks and open questions

1. **Tool-list filtering is observable to the LLM.** When a patrol runs, the
   tool list it sees depends on auth state at session-start. If auth flips
   mid-session, the in-flight session keeps its tool list. That's acceptable
   — the next session re-derives. But it means a long-running session that
   spans an auth expiry will keep trying Work IQ tools and getting
   `auth_required` until it ends. Probably fine; document the behavior.

2. **`WorkIqAuthCacheUpdated` triggers tool re-publish.** Whenever a fresh
   cache arrives the agent re-publishes the tool list with the workiq-*
   tools restored. Currently the bridge publishes on
   `ConnectServerAsync` completion and on management refreshes; adding a
   trigger on cache-arrival is one new code path. Make sure the throttling
   from Phase 2 still applies so a flood of cache-updates doesn't flood the
   tool-list topic.

3. **Health tracker is per-process.** If the agent pod restarts, the tracker
   starts in an unknown state. We can recover by inspecting the cache file
   on disk at startup: present + non-empty → assume healthy until proven
   otherwise. Document the startup recovery.

4. **Double-click guard could race with state-change.** The component-level
   guard is fine for human-speed clicking; if we ever programmatically drive
   the flow (e.g., auto-reconnect on banner click), we need stronger
   serialization. Leave that to a follow-up if it ever matters.

5. **Telemetry metric is optional.** If `RockBot.Telemetry` doesn't have a
   clean hook for custom gauges, ship just the log line and revisit when the
   observability work catches up. Don't block Phase 5 on telemetry plumbing.

## Acceptance criteria

- [ ] A tool call against a Work IQ server with a revoked refresh token
  returns `ToolError { Code=auth_required, IsRetryable=false }` with a
  message that mentions "Reconnect M365" — verifiable in unit tests and in
  the docker-compose smoke (extend Smoke #3 from Phase 4 with chat-side
  assertion).
- [ ] The agent's published tool list excludes `workiq-*` tools while
  auth is unhealthy, and re-includes them within ~1 second of a fresh
  `WorkIqAuthCacheUpdated` arriving.
- [ ] Patrol-run logs show patrols skipping WorkIQ-dependent steps
  cleanly (no `execution_failed` retries) while auth is unhealthy.
- [ ] Blazor's "Connect M365" and "Reconnect" buttons are no-ops on
  rapid second clicks while a flow is in progress.
- [ ] `deploy/workiq-setup.md` includes the re-consent flow and the
  scheduled-task-behavior subsections.
- [ ] One agent log line on every health flip:
  `WorkIQ auth health changed: Healthy=<old> → <new>. Reason: <code>.`

## Implementation order

1. Add `WorkIqHealthTracker` + DI registration + log emission. No behavior
   change yet — just the state holder.
2. Wire `MsalTokenProvider` + `TokenCacheStore` into the tracker.
3. Add the `FindReauthRequired` / `FindNotAuthenticated` walkers and catch
   clauses in `McpBridgeService`. Tests.
4. Add tool-list filtering in the bridge's publish path. Tests.
5. Blazor button guards. Tests.
6. Update Smoke #3 in `deploy/workiq-setup.md` (or `workiq-phase4-plan.md`
   if Phase 4 hasn't landed yet) to include the new tool error message
   assertion.
7. Add the re-consent flow + scheduled-task subsections to
   `deploy/workiq-setup.md`.
8. Open the PR. No new external dependencies; everything is unit-tested.

## Out of scope (genuinely)

- **Auto-reconnect.** When the banner appears, the user clicks Reconnect.
  We do not automatically initiate the flow because device-code requires
  the user's eyes on the screen anyway.
- **Multi-user expiry handling.** Single-user model.
- **Per-tool re-auth.** A WorkIQ permission added later (e.g.,
  `WorkIQ-Teams`) requires re-consent to add scopes. That re-consent
  request is a separate UX from "your existing scopes expired" and is
  worth its own follow-up issue.
- **Token-broker microservice.** Still deferred.

## References

- [`design/workiq-integration.md`](workiq-integration.md) — original design
- [`design/workiq-phase4-plan.md`](workiq-phase4-plan.md) — preceding phase
- [PR #442](https://github.com/MarimerLLC/rockbot/pull/442) — Phase 1+2 code
- [PR #443](https://github.com/MarimerLLC/rockbot/pull/443) — Phase 3 code
- `src/RockBot.Agent/McpBridge/McpBridgeService.cs:1601` — current
  `FindAuthChallenge` walker that the new `FindReauthRequired` walker mirrors
- `src/RockBot.Agent/McpBridge/Auth/MsalTokenProvider.cs:65` — current
  `MsalUiRequiredException` catch that the new health tracker hooks into
