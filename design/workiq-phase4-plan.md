# WorkIQ Phase 4 — Entra app registration and end-to-end wire-up

Tracking issue: [#441](https://github.com/MarimerLLC/rockbot/issues/441).
Builds on the foundations landed in [#442](https://github.com/MarimerLLC/rockbot/pull/442)
(Phase 1+2 bridge auth + MSAL plumbing) and
[#443](https://github.com/MarimerLLC/rockbot/pull/443)
(Phase 3 UI-tier device-code flow).

## Goal

Prove the end-to-end Work IQ flow locally with `docker compose`: a single Blazor
consent ceremony causes a `workiq-mail` / `workiq-calendar` tool call to return
real Microsoft 365 data, and silent token refresh works across an access-token
TTL boundary without any manual intervention. Land the documentation needed for
an operator to reproduce the ceremony from scratch.

**All smoke testing in this phase happens against the local docker-compose
stack at `deploy/docker-compose/docker-compose.yml`.** Cluster rollout is a
follow-on step — see the appendix — and is deliberately not part of acceptance.

## What is and is not in this phase

| In | Out |
|---|---|
| Entra app registration walkthrough (manual, documented in `deploy/workiq-setup.md`) | Re-consent UX polish (Phase 5) |
| `.env` and `docker-compose.yml` plumbing for `WORKIQ_*` variables | Multi-user Work IQ access (v2) |
| MCP server registration UX for workiq-mail / workiq-calendar | App-only / client-credentials path |
| End-to-end smoke on docker-compose locally | A token-broker microservice |
| First-run troubleshooting notes for common failure modes | Other Work IQ servers (Teams, SharePoint, OneDrive, etc.) — same pattern, separate ticket |
| Optional: helm chart docs noting that production rollout reuses the local setup | Live cluster smoke — operator runs the local procedure first, then promotes |

## Why local-only

- The compose stack runs the same code paths the cluster runs: same agent image,
  same Blazor image, same RabbitMQ, same `McpBridgeService`, same
  `TokenCacheStore`, same `WorkIqDeviceCodeFlow`. If the flow works on compose
  it works on the cluster, modulo the helm chart's env-var wiring (which is
  unit-tested via `helm template`).
- The agent's MSAL cache file lives at `/data/agent/secrets/workiq-cache.bin`
  on a named docker volume. Throwaway by design — `docker compose down -v`
  resets state cleanly.
- The Entra app registration itself is a one-time global act regardless of
  where the consent runs, so doing it once and pointing both compose and (later)
  cluster at the same tenant/client costs nothing extra.
- A failed smoke against the live cluster is painful (operator pod restart,
  potentially user-facing). A failed smoke against compose is `docker compose
  down -v` and try again.

## Prerequisites

Before starting Phase 4, confirm:

- Microsoft 365 Copilot license assigned to Rocky's M365 account.
- Tenant admin (or app-registration-creator) access in the Entra tenant Rocky's
  account belongs to.
- Phase 1+2+3 merged on `main` (already done — PRs #442, #443).
- Local docker-compose stack runs: `cd deploy/docker-compose && docker compose
  up rabbitmq agent blazor` works (the introspection-mcp and scripts-manager
  services are not required for the smoke).
- Browser on the same machine as compose (or able to reach `localhost:8080`).

## Step-by-step (operator runbook, draft for `deploy/workiq-setup.md`)

### 1. Register a public-client app in Entra

1. Sign in to <https://portal.azure.com> as a tenant admin.
2. Navigate to **Microsoft Entra ID → App registrations → New registration**.
3. **Name**: `RockBot WorkIQ`. **Supported account types**: *Accounts in this
   organizational directory only (single tenant)* — multi-tenant adds nothing
   while we have one user.
4. **Redirect URI**: leave blank. Device-code flow does not use redirects.
5. Click **Register**. Copy the **Application (client) ID** and **Directory
   (tenant) ID** from the Overview page — these become `WORKIQ_CLIENT_ID`
   and `WORKIQ_TENANT_ID` in `.env` (compose-local) and `workiq.clientId` /
   `workiq.tenantId` in helm values (later, for cluster rollout).

### 2. Mark the app as a public client

1. **Authentication → Advanced settings → Allow public client flows** → **Yes**. Save.

This is what enables device-code; MSAL refuses to run public flows without it.

### 3. Grant delegated WorkIQ permissions

1. **API permissions → Add a permission → Microsoft Graph → Delegated
   permissions**, search for and add:
   - `WorkIQ-MailServer` (read mail context)
   - `WorkIQ-Calendar` (read calendar context)
   - Additional scopes as later phases enable more servers.
2. **Grant admin consent for <tenant>** so the agent receives an
   admin-consented token rather than failing the silent-refresh path on first
   use. Without this, MSAL silent refresh hits `AADSTS65001` on every call.

### 4. Configure docker-compose

Add a `.env` file alongside `deploy/docker-compose/docker-compose.yml` (or
extend the existing one):

```bash
WORKIQ_TENANT_ID=<tenant-guid-from-step-1>
WORKIQ_CLIENT_ID=<client-guid-from-step-1>
WORKIQ_SCOPES=WorkIQ-MailServer/.default,WorkIQ-Calendar/.default
```

This phase adds the wiring for these vars in compose:

- **`docker-compose.yml` — `agent` service** gains:
  ```yaml
  WorkIQ__TenantId: ${WORKIQ_TENANT_ID:-}
  WorkIQ__ClientId: ${WORKIQ_CLIENT_ID:-}
  WorkIQ__Scopes:   ${WORKIQ_SCOPES:-}
  ```
  The agent's `Program.cs` already gates `AddWorkIqAuth` on these being
  non-empty (Phase 2), so an unset `.env` is a no-op.

- **`docker-compose.yml` — `blazor` service** gains the same three keys. The
  Blazor `Program.cs` always calls `AddWorkIqAuthClient` (Phase 3); the flow
  fails fast with `not_configured` when the values are absent, which keeps
  Blazor functional for non-WorkIQ operators.

- **`agent-init` service** creates `/data/agent/secrets/` and chmods it 0700,
  mirroring the helm chart's init container. The chmod has to land before the
  agent process starts so the first `SetUnixFileMode(0600)` write succeeds.

### 5. First consent

1. `cd deploy/docker-compose && docker compose up rabbitmq agent blazor` (or
   `docker compose up -d` for the full stack).
2. Open `http://localhost:8080` in a browser.
3. Click the **M365** pill in the chat header.
4. Click **Connect M365** in the modal.
5. Copy the displayed user code, follow the link to
   `microsoft.com/devicelogin` in another tab, paste the code, sign in as
   Rocky.
6. Approve the consent prompt.
7. Wait for the modal to show **"Connected to Microsoft 365."**

Verify on the agent container:

```bash
docker compose exec agent ls -la /data/agent/secrets/
# expect workiq-cache.bin present, mode -rw------- (owner = rockbot)

# confirm the cache size is non-trivial (a populated MSAL v3 cache is a few KB)
docker compose exec agent stat -c '%s' /data/agent/secrets/workiq-cache.bin
```

### 6. Register the Work IQ MCP servers

Per the design doc's open question — *Whether to ship Work IQ servers in the
default seeded mcp.json or require the operator to register them manually after
consent (probably the latter)* — Phase 4 takes the manual path. Rationale:

- Helm/compose seeds with `workiq-*` URLs would silently fail with
  `auth_required` errors on every operator who has not completed consent.
  Adds noise to first-run setups that do not need WorkIQ at all.
- The Phase 1 `auth_required` ToolError code is descriptive enough that an
  agent who tries an unregistered Work IQ tool will surface a useful error.
- Registration via the existing `mcp_register_server` management tool (Phase
  1's `McpRegisterServerRequest`) is one call per server.

Two flavors of registration are available depending on operator preference:

**(a) Via the agent's MCP management tool** (use this for the smoke). Drive
through the chat or via direct bus message; the simplest path is to ask the
agent in chat:

```text
Register a new MCP server named "workiq-mail" of type streamable-http at
url https://agent365.svc.cloud.microsoft/agents/tenants/<TENANT>/servers/mcp_MailTools
with auth profile "workiq".
```

The agent's `mcp_register_server` tool handles the bus call. Repeat for
`workiq-calendar` with `mcp_CalendarTools`.

**(b) Direct `mcp.json` edit on the docker volume** (faster for iteration):

```bash
docker compose exec agent sh -c 'cat /data/agent/mcp.json'
# add the two entries with auth.profile=workiq, then put back
docker compose exec -T agent sh -c 'cat > /data/agent/mcp.json' < new-mcp.json
```

Either path triggers the bridge's config watcher; tools become visible within
~500ms.

### 7. End-to-end smoke

Once both servers are registered, run a smoke test sequence.

**Smoke #1 — direct tool call from a chat session.**
In Blazor at `http://localhost:8080`: "What's in my inbox right now?". Expect
the agent to call `workiq-mail`'s search-or-list tool, return results, and not
surface `auth_required`. Confirm in `docker compose logs agent`:

```text
[Information] → MCP workiq-mail/<tool> args=...
[Information] ← MCP workiq-mail/<tool> OK in 123ms (... chars)
```

**Smoke #2 — silent refresh after access-token expiry.**
Wait approximately 65 minutes (longer than the access-token TTL) without
touching the system. The compose stack can run unattended. Then issue another
tool call from chat. Expect:

- Agent log: `TokenCacheStore` debug entry `Persisted MSAL cache rotation`.
- No re-consent prompt anywhere in Blazor.
- Tool call succeeds.
- `docker compose exec agent stat -c '%Y' /data/agent/secrets/workiq-cache.bin`
  shows a recently-rotated mtime.

**Smoke #3 — re-consent ceremony after revoking the refresh token.**
On the Entra portal, **Users → Rocky → Sign-ins → Revoke sessions**. Next
agent tool call should:

- Fail with `auth_required` ToolError code (visible in chat and in agent logs).
- Agent publishes `WorkIqAuthExpired` on `auth.workiq.expired`.
- Blazor's banner appears within ~1 second (the listener is a hot subscriber).
- Clicking Reconnect → device-code → success → token cache replaced.
- Following tool call succeeds.

To reset between full smoke runs (e.g., to retry from scratch):

```bash
docker compose down -v   # wipes volumes including workiq-cache.bin
docker compose up rabbitmq agent blazor
```

## Files / artifacts produced by this phase

| Path | Status | Purpose |
|---|---|---|
| `deploy/workiq-setup.md` | new | Operator runbook (sections 1–7 above), refined from the actual smoke run |
| `deploy/docker-compose/docker-compose.yml` | edited | Add `WorkIQ__*` env on `agent` and `blazor`; add `mkdir /data/agent/secrets && chmod 700` to `agent-init` |
| `deploy/docker-compose/.env.example` | new (if missing) | Document `WORKIQ_TENANT_ID/CLIENT_ID/SCOPES` placeholders |
| `design/workiq-phase4-plan.md` | this file | Planning artifact; delete from `design/` after Phase 4 lands if `deploy/workiq-setup.md` covers the same ground |

No source-code changes to the C# projects are expected. The phase is compose
plumbing + verification + documentation.

## Risks and open questions

1. **Work IQ scope strings may differ from `WorkIQ-MailServer/.default`.**
   The MS Learn docs use this shape but the preview product has shifted. Treat
   the runbook's strings as draft; verify against the live `WorkIQ-*` permission
   names visible in Entra when you add them in step 3.1. Update the doc + the
   compose `.env.example` if Microsoft has changed the convention.

2. **Admin-consent step (3.2) is the most likely first-run footgun.** Without
   admin consent, MSAL silent refresh from the compose agent will hit
   `AADSTS65001` and the bridge will surface `auth_required` on every tool
   call. The runbook should call this out prominently before the consent step.

3. **`workiq-cache.bin` lifetime under compose.** The cache lives on the
   `agent-data` named volume. `docker compose down` (without `-v`) preserves
   the cache between stack restarts — that's intentional for ongoing local
   testing. `docker compose down -v` wipes it and forces re-consent on next
   bring-up; that's the documented reset path for smoke #3 retries.

4. **No automated test for the live flow.** Phase 4 acceptance is manual
   smoke against compose plus the existing unit-test coverage from Phases 1–3.
   Cost of a full integration test that hits real Entra is high (per-environment
   client secrets, tenant access from CI) and value is low while we have one
   user. Re-evaluate at v2.

5. **Cluster rollout is a separate step** — see appendix. Doing it as a
   separate ticket isolates "did Microsoft Work IQ work?" (this phase) from
   "did the helm chart roll out cleanly to my cluster?" (operational).

6. **Smoke #2 takes ~1 hour to run.** That is unavoidable when proving real
   silent refresh against the real identity service. It runs unattended; budget
   the wait into the phase rather than skipping it.

## Acceptance criteria

- [ ] `deploy/workiq-setup.md` exists and walks an operator through sections 1–7
  using only docker-compose, without external lookups.
- [ ] `deploy/docker-compose/docker-compose.yml` carries the `WORKIQ_*` env
  plumbing on both `agent` and `blazor` services.
- [ ] `deploy/docker-compose/.env.example` (or the existing equivalent)
  documents the three `WORKIQ_*` variables.
- [ ] Smoke #1 succeeds — chat-driven `workiq-mail` call returns inbox content
  against the compose stack.
- [ ] Smoke #2 succeeds — silent refresh happens transparently across a TTL
  boundary, observable in `docker compose logs agent` and via the cache file's
  mtime.
- [ ] Smoke #3 succeeds — re-consent ceremony recovers from a revoked token
  without any agent restart; the Blazor banner appears and the Reconnect button
  works.
- [ ] `docker compose exec agent ls -la /data/agent/secrets/` confirms cache
  presence, mode 0600, and rotation.
- [ ] No credential material appears in `docker compose logs` during smoke
  (eyeball-checked or grepped).

## Implementation order

1. Run the Entra registration ceremony in the live tenant (sections 1–3). Pin
   down the exact scope strings — they go into `.env.example`.
2. Edit `deploy/docker-compose/docker-compose.yml` to add the `WORKIQ_*` env
   wiring and the `agent-init` secrets-directory step.
3. Add `.env.example` (or extend the existing one) with the three new
   variables.
4. `docker compose down -v && docker compose up rabbitmq agent blazor`
   (clean slate).
5. Drive the first Blazor consent ceremony; verify cache file lands on the
   agent volume with mode 0600.
6. Register `workiq-mail` and `workiq-calendar` via the agent's
   `mcp_register_server` tool.
7. Run Smoke #1.
8. Start Smoke #2 (~1 hour wait); use that time to draft `deploy/workiq-setup.md`
   from the steps actually taken.
9. Run Smoke #3.
10. Finalize `deploy/workiq-setup.md` based on what actually worked and what
    surprised you. The runbook is the deliverable.
11. Open the PR with the new runbook, the compose changes, and any small
    doc tweaks discovered during smoke.

## Appendix — cluster rollout (out of acceptance scope)

After the local smoke passes, promote to the live cluster as a separate
exercise:

1. Update `deploy/values.personal.yaml.live` (gitignored) with the same
   tenant/client/scopes values that worked in compose:
   ```yaml
   workiq:
     enabled: true
     tenantId: "..."
     clientId: "..."
     scopes:
       - "WorkIQ-MailServer/.default"
       - "WorkIQ-Calendar/.default"
   ```
2. Per the memory's "Pull live helm values before upgrade" note, snapshot
   live values first: `helm get values rockbot -n default >
   /tmp/live-values.yaml`. Layer the `workiq:` block on top.
3. `helm upgrade rockbot deploy/helm/rockbot -n default -f /tmp/merged.yaml`.
4. Verify pod env: `kubectl exec -n rockbot <agent-pod> --container=agent --
   env | grep WorkIQ`.
5. Re-run the consent ceremony against the cluster's Blazor (the same Entra
   app registration works for both — the consent populates the cluster's
   `workiq-cache.bin` independently of compose's).
6. Re-register `workiq-mail` / `workiq-calendar` against the cluster's agent
   (it's a different `mcp.json` on a different PVC).

Cluster rollout failures fall back to compose: the operator already has a
working local stack to debug against without disrupting the live pod.

## References

- [`design/workiq-integration.md`](workiq-integration.md) — original design doc
- [PR #442](https://github.com/MarimerLLC/rockbot/pull/442) — Phase 1+2
- [PR #443](https://github.com/MarimerLLC/rockbot/pull/443) — Phase 3
- [Work IQ MCP overview (Microsoft Learn)](https://learn.microsoft.com/en-us/microsoft-agent-365/tooling-servers-overview)
- [MSAL device-code flow docs](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-device-code)
- `deploy/docker-compose/docker-compose.yml` — the compose stack that smoke tests run against
