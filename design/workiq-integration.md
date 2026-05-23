# WorkIQ Integration

## Why

Microsoft's **Work IQ** (preview, May 2026) is the Agent 365 catalog of MCP servers that
expose M365 data — Mail, Calendar, Teams, SharePoint, OneDrive, Word, Copilot Search, User,
Dataverse — over HTTP/JSON-RPC at
`https://agent365.svc.cloud.microsoft/agents/tenants/{tenantId}/servers/{server}`.

The differentiator vs. plain Microsoft Graph is the Copilot-grounded search and the unified
"intelligence layer" semantic ranking. If we want the agent to reason over Rocky's M365
context the way Copilot does, this is the path; if we just need Graph CRUD, the existing
ms-365 MCP server is fine.

This doc covers **how to make Work IQ usable from the RockBot agent**, which is a headless
pod with no browser and no human at the keyboard.

## What makes it awkward

Three properties of Work IQ don't line up with how the agent runs:

1. **Per-user delegated auth.** Every Work IQ call requires a token issued to a specific
   user who holds a Microsoft 365 Copilot license. App-only / client-credentials is not
   supported. The agent must call as *Rocky*, not as itself.
2. **Interactive OAuth assumed.** The standard MCP-client pattern for Work IQ
   (Claude Code, GH Copilot CLI, VS Code) uses OAuth 2.1 with PKCE and a loopback
   redirect (`http://localhost:8080/callback`). The agent pod has no browser and no
   loopback listener that a human can reach.
3. **HTTP transport with expiring bearer.** Tokens expire in ~1 hour. The existing
   `McpBridgeService` sets headers once on `HttpClient.DefaultRequestHeaders` at
   connect time, which would 401 silently after the first hour.

## Architecture

Split responsibility cleanly along the trust boundary that already exists:

```
Blazor / CLI (UI tier)          Agent pod (compute tier)
─────────────────────           ────────────────────────
User clicks "Connect M365"
    │
    ▼
MSAL public client
  interactive (Blazor) or
  device code (CLI)
    │
    ▼
AuthenticationResult
+ serialized MSAL cache
    │
    │  WorkIqAuthCacheUpdated
    │  topic: auth.workiq.cache
    │ ────────────────────────►│
    │                          │  TokenCacheStore
    │                          │  writes /data/agent/secrets/workiq-cache.bin
    │                          │
    │                          │
    │                          │  Per tool call:
    │                          │  ┌──────────────────────────────┐
    │                          │  │ McpBridgeService              │
    │                          │  │   HttpClient w/                │
    │                          │  │   BearerInjectionHandler ─────┼─► WorkIQ
    │                          │  │     │                          │   HTTP/JSON-RPC
    │                          │  │     ▼                          │
    │                          │  │   ITokenProvider               │
    │                          │  │     AcquireTokenSilent         │
    │                          │  │     (refreshes via cached RT)  │
    │                          │  │     persists rotated cache     │
    │                          │  └──────────────────────────────┘
```

The UI tier owns *only* the interactive consent. The agent tier owns *everything*
about token storage and refresh — same as it owns every other long-lived credential.

### Why the UI does the auth

The UI containers (Blazor, CLI) are the only RockBot processes that can reach a
human's browser. They already authenticate the human user for their own purposes,
so an additional MSAL flow is a natural extension, not a new privilege.

### Why the agent owns storage

The agent's PVC at `/data/agent` is already the credential-bearing volume. It holds
`mcp.json`, the LLM API keys via env, the memory store, profile files. The UI pods
mount no PVCs at all and would need new RBAC to write k8s Secrets (a security smell —
UI tier becomes credential-issuing). Keeping storage in the agent pod requires no
new permissions and no new trust boundary.

### Why not stash in mcp.json

`mcp.json` is configuration that the bridge re-reads, re-serializes, and dedupes on
every server registration. Embedding refresh tokens there would mean every config
mutation rewrites the token, and any future config export/sync would leak credentials.
Tokens go in a separate file under `/data/agent/secrets/`.

## Token lifecycle

1. **Initial consent** — user opens Blazor (or runs `rockbot auth workiq` in the CLI),
   completes MSAL interactive / device-code flow against the registered RockBot Entra
   app with `WorkIQ-MailServer`, `WorkIQ-Calendar`, etc. delegated scopes. MSAL returns
   an `AuthenticationResult` and populates its in-memory token cache.

2. **Cache transfer** — UI serializes the MSAL cache, publishes
   `WorkIqAuthCacheUpdated { CacheBytes, AccountId, Scopes }` on `auth.workiq.cache`.
   The bus message is the *only* time the refresh token traverses the network outside
   of Microsoft's domain. Bus transport is already trusted with everything else
   sensitive (LLM prompts, working memory).

3. **Persistence** — agent's `TokenCacheStore` subscribes to that topic, writes the
   cache bytes to `/data/agent/secrets/workiq-cache.bin` (mode 0600), and loads them
   into its in-memory `MsalCacheHelper`.

4. **Per-request acquisition** — when the bridge sends an HTTP request to a Work IQ
   server, a `DelegatingHandler` calls `ITokenProvider.GetTokenAsync(scopes)` which
   issues `AcquireTokenSilent` against the cache. MSAL handles refresh-token rotation
   automatically; the handler writes the updated cache back to disk on each rotation.

5. **Re-consent** — when the refresh token is finally invalidated (Entra default ~90
   days idle, sooner on password change or revocation), `AcquireTokenSilent` throws
   `MsalUiRequiredException`. The token provider publishes
   `WorkIqAuthExpired { AccountId }` on a topic the UI subscribes to. Next time the
   user opens Blazor, it shows a "Reconnect M365" prompt. Until reconnect, Work IQ
   tool calls fail with a clear `ToolError` that includes the re-auth instruction.

## Bridge changes

Two small additions to `RockBot.Agent.McpBridge`:

### 1. `McpBridgeServerConfig.Auth`

```csharp
public sealed class McpServerAuthConfig
{
    /// <summary>
    /// Named auth profile resolved by ITokenProviderRegistry.
    /// e.g. "workiq" → MsalTokenProvider with the WorkIQ scopes.
    /// </summary>
    public required string Profile { get; set; }
}

public sealed class McpBridgeServerConfig
{
    // ... existing fields ...
    public McpServerAuthConfig? Auth { get; set; }
}
```

### 2. Per-request bearer injection in `ConnectServerAsync`

Today (`McpBridgeService.cs:374-388`), headers are set once on
`httpClient.DefaultRequestHeaders`. For auth-bearing servers, replace that with a
`DelegatingHandler`:

```csharp
if (config.Auth is not null)
{
    var tokenProvider = _tokenProviders.Get(config.Auth.Profile);
    var handler = new BearerInjectionHandler(tokenProvider)
    {
        InnerHandler = new SocketsHttpHandler()
    };
    var httpClient = new HttpClient(handler);
    // static headers (if any) still apply
    foreach (var (key, rawValue) in config.Headers) { ... }
    transport = new HttpClientTransport(transportOptions, httpClient, ..., ownsHttpClient: true);
}
```

`BearerInjectionHandler.SendAsync` calls the token provider on every request, so
refresh happens transparently. On a `401`, the handler invalidates the cached
access token and retries once before surfacing the failure.

### 3. `ITokenProvider` and registry

```csharp
public interface ITokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

public interface ITokenProviderRegistry
{
    ITokenProvider Get(string profile);
}
```

The `MsalTokenProvider` implementation wraps `IPublicClientApplication` with an MSAL
cache backed by `TokenCacheStore`. Registered in DI with the configured tenant ID,
client ID, and scopes (sourced from the agent's appsettings / configmap, not from
`mcp.json`).

## mcp.json shape

Credentials never appear in `mcp.json`:

```json
{
  "mcpServers": {
    "workiq-mail": {
      "type": "streamable-http",
      "url": "https://agent365.svc.cloud.microsoft/agents/tenants/${WORKIQ_TENANT_ID}/servers/mcp_MailTools",
      "auth": { "profile": "workiq" }
    },
    "workiq-calendar": {
      "type": "streamable-http",
      "url": "https://agent365.svc.cloud.microsoft/agents/tenants/${WORKIQ_TENANT_ID}/servers/mcp_CalendarTools",
      "auth": { "profile": "workiq" }
    }
  }
}
```

`${WORKIQ_TENANT_ID}` resolves via the existing `ExpandEnvVars` path in
`McpBridgeService`.

## Entra app registration

A single **RockBot** public-client app registration handles all Work IQ servers:

- **Platform:** Mobile and desktop applications.
- **Redirect URIs:**
  - `http://localhost:8080/callback` — for Blazor's interactive flow when run locally.
  - No redirect for device-code flow (CLI path).
- **API permissions (delegated):** one `WorkIQ-*` permission per server we want to
  enable — `WorkIQ-MailServer`, `WorkIQ-Calendar`, `WorkIQ-Teams`, `WorkIQ-SharePoint`,
  `WorkIQ-OneDrive`, `WorkIQ-CopilotSearch`, etc. Admin consent granted at registration
  time.
- **Tenant:** single-tenant (Rocky's). Multi-tenant adds nothing while we have one user.

Client ID and tenant ID land in the agent's configmap; no secret material is needed
(public client). Blazor and CLI share the same client ID.

## Storage layout

```
/data/agent/
├── mcp.json                       (existing, never holds tokens)
├── secrets/
│   └── workiq-cache.bin           (MSAL serialized cache, mode 0600)
└── ...
```

`secrets/` is new but lives on the same PVC as everything else sensitive. Same trust
boundary, same backup story, no new infrastructure.

## Failure modes

| Failure | Detection | Response |
|---|---|---|
| Refresh token revoked / expired | `MsalUiRequiredException` from `AcquireTokenSilent` | Publish `WorkIqAuthExpired`, surface re-consent prompt in Blazor next session, fail the tool call with actionable `ToolError` |
| Access token rejected mid-session (401) | `BearerInjectionHandler` sees `401` | Force `AcquireTokenSilent` with `forceRefresh: true`, retry once, then surface as above |
| `workiq-cache.bin` missing or corrupt at startup | `TokenCacheStore` load fails | Bridge logs warning, all `workiq-*` servers connect but fail tool calls with "not authenticated" until UI publishes a fresh cache |
| Copilot license removed from user | Work IQ returns 403 | Surface as `ToolError` with non-retryable code; license restoration is out of band |
| Tenant admin blocks the server in M365 admin center | Work IQ returns 403 or connection fails | Same as above |

## Out of scope (for v1)

- **Multi-user Work IQ access.** This design covers one user (Rocky). Per-user
  delegation through the existing UserProxy plumbing is a v2 conversation that
  intersects with how UserProxy already carries identity.
- **Token-broker microservice.** A separate pod that holds the MSAL cache and exposes
  `GET /token/{profile}` is the natural next step if we ever need stricter isolation
  or want non-agent consumers (e.g. dream pods) to share the same identity. Not
  needed while everything that calls Work IQ runs inside the agent process.
- **App-only / client-credentials.** Work IQ docs are clear that delegated is the
  supported mode. If a future preview adds app-only support, the architecture above
  collapses dramatically — agent gets its own M365 identity, no UI involvement, no
  token cache transfer. Worth re-checking at each preview milestone.

## Open questions

1. **Is Copilot licensing worth it for what Work IQ adds over Graph?** Work IQ's value
   is Copilot-grounded semantic search and ranking. If the agent's M365 needs are
   primarily CRUD (read mail, send mail, create event), the existing ms-365 MCP
   server hits Graph directly and avoids the per-user Copilot license. Decision
   should precede the engineering work.

2. **How does this interact with `gsd:patrol` / scheduled tasks running while Rocky
   is asleep?** Silent refresh keeps tokens fresh for ~90 days of activity. But if
   the refresh token expires while no human is around to re-consent, scheduled M365
   tasks will fail until next interactive session. Acceptable, but worth being
   explicit about.

3. **Preview risk.** Work IQ is in preview as of May 2026. The HTTP endpoint shape,
   the MCP OAuth flavor, and the available scopes may all shift before GA. Building
   now means absorbing churn; waiting for GA means going without. Worth a check-in
   each time Microsoft updates the preview.

## References

- [Work IQ MCP overview (Microsoft Learn)](https://learn.microsoft.com/en-us/microsoft-agent-365/tooling-servers-overview)
- [microsoft/work-iq on GitHub](https://github.com/microsoft/work-iq)
- [Using Work IQ from 3rd party apps (candede.com)](https://candede.com/articles/use-work-iq-mcp-servers-from-3rd-party-apps)
- [`design/mcp-bridge.md`](mcp-bridge.md) — the bridge this integration extends
- [`design/security.md`](security.md) — trust boundaries this design respects
