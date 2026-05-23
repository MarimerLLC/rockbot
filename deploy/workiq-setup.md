# Work IQ — operational runbook

This document is the operational reference for the WorkIQ integration. It covers
how the agent authenticates to Microsoft 365, what users and operators see
during normal operation, and how to recover from common failure modes.

> Phase 4 will populate the bulk of this runbook (initial setup, smoke tests,
> Entra registration walkthrough). Phase 5 added the **Re-consent flow** and
> **Scheduled tasks during expiry** sections below. See
> [`design/workiq-phase4-plan.md`](../design/workiq-phase4-plan.md) and
> [`design/workiq-phase5-plan.md`](../design/workiq-phase5-plan.md) for context.

## Re-consent flow

WorkIQ tokens are refreshed silently by the agent. When the underlying refresh
token is rejected by Microsoft (typically because it has been revoked, the
account password changed, or admin policy invalidated the consent), the agent
cannot recover automatically — the user must complete a fresh device-code flow
in the Blazor UI.

### What the user sees

1. **In the Blazor app**, a yellow banner appears at the top of every page:

   > **M365 connection expired.** *<reason from MSAL>*  [Reconnect] [✕]

   The banner stays up across navigations until the user clicks **Reconnect**
   (or dismisses it with **✕**). A second M365 expiry while dismissed will
   re-show the banner.

2. **In chat**, the next message that would have used a Work IQ tool comes
   back with an `auth_required` error whose text reads:

   > Microsoft 365 connection has expired. Open the Blazor app and click
   > 'Reconnect M365' to restore access. Work IQ tools will fail until
   > reconnection is complete.

   The LLM treats this as non-retryable and surfaces it to the user
   verbatim, reinforcing the banner.

### Click sequence to recover

1. Click **Reconnect** in the yellow banner (or open the M365 pill menu and
   choose Reconnect).
2. A modal opens with a device code and a sign-in URL.
3. Open the URL in another browser tab (or on your phone), paste the code, and
   complete Microsoft sign-in including any MFA challenge.
4. The modal flips to **Connected to Microsoft 365.** within a few seconds.
5. The banner clears across all open Blazor circuits.

End-to-end time when the user is fast: ~30 seconds. The bottleneck is the
Microsoft sign-in page, not the agent — once the user finishes there, the
agent receives the fresh cache within ~1 second and Work IQ tools become
available again.

### What the agent does during expiry

- The agent's `WorkIqHealthTracker` flips to **unhealthy** the moment silent
  refresh fails. It republishes the MCP tool list with WorkIQ-backed servers
  removed, so the LLM never sees those tools in subsequent sessions until
  recovery. Direct tool calls that *do* still hit WorkIQ get back the
  `auth_required` error described above.
- Other (non-WorkIQ) MCP tools are unaffected.
- In-flight LLM sessions keep their original tool list. A session that spans
  the expiry will continue to try WorkIQ tools and receive `auth_required`
  until it ends; the next session will not see those tools at all.

### Verifying recovery (operator)

Look for the agent log line that fires on every transition:

```
WorkIQ auth health changed: Healthy=False → True. Reason: cache_updated_from_ui.
```

Alternatively check the cache file's mtime on the agent's PVC:

```bash
POD=$(kubectl get pod -n rockbot -l app=rockbot-agent -o jsonpath='{.items[0].metadata.name}')
MSYS_NO_PATHCONV=1 kubectl exec -n rockbot "$POD" -c agent -- ls -la /data/agent/secrets/workiq-cache.bin
```

A recent mtime confirms the new cache was written.

## Scheduled tasks during expiry

Patrols and other scheduled tasks share the agent's MCP tool list. The hide-
tools mechanism described above applies to them as well:

- While auth is unhealthy, WorkIQ-backed tools (e.g. `workiq-outlook-*`,
  `workiq-onedrive-*`) are filtered out of the tool list that scheduled-task
  sessions see at startup.
- Patrols that would normally use those tools naturally skip those steps —
  they see no WorkIQ tools, so the LLM never attempts them, and patrol
  summaries do not contain `execution_failed` retries.
- Patrols whose entire purpose is WorkIQ (e.g., an inbox-triage patrol) will
  produce a short "nothing to do" summary while expired, rather than a long
  error trail.
- The moment the user reconnects, the next patrol cycle (or in-flight session
  that re-derives its tool list) gains the WorkIQ tools back. No restart
  needed.

If you see a patrol attempting WorkIQ tools while the banner is up, that
means the patrol started *before* the auth flip — its tool list is frozen for
the life of the session. The next cycle will be clean.
