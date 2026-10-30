# MCP OAuth sign-in

Tracking issue: [#738](https://github.com/MarimerLLC/rockbot/issues/738)

## Why

The MCP authorization spec lets a server protect itself with OAuth 2.1. A request with no token
gets `401` and `WWW-Authenticate: Bearer resource_metadata="…"`. The client follows the
protected-resource metadata (RFC 9728) to the server's authorization server (RFC 8414). It
registers as a client: pre-registered, a client id metadata document, or dynamic registration.
It then sends the user through authorization-code + PKCE in a browser, and calls the server as
that user. [marimerllc/mcp2-oauth](https://github.com/marimerllc/mcp2-oauth) is an Entra-backed
example.

Before this, the only authenticated path in the bridge was the Work IQ profile
([`workiq-integration.md`](workiq-integration.md)). That path is MSAL-only, device-code-only
and named in code, and the Blazor UI carries M365-specific login for it. This adds a
**generic, per-server** path driven by what the server itself advertises.

## The split

The agent pod runs the OAuth client and has no browser. The user interfaces (Blazor, CLI) have a
browser and must stay credential-free. So the flow splits on the one step that needs a person:

```
User interface (Blazor / CLI)            Agent pod (McpBridgeService)
─────────────────────────────            ────────────────────────────
                                          connect → 401 → background callback:
                                            mark "sign-in required"
            ◄──── McpSignInStatus(required) ── (broadcast, user.response.{agent})
"Sign in to X" ─ McpSignInStartRequest ──►
   (redirectUri = this UI's callback)     dedicated connect with ClientOAuthOptions:
                                            PRM → AS metadata → client registration
                                            → PKCE verifier (stays here)
            ◄── McpSignInStartResponse ────  authorization URL (carries state)
open URL in browser
  … user signs in at the AS …
browser → redirectUri?code&state&iss
  ─────────── McpSignInCallback ─────────►  matched by state; SDK checks state + iss,
                                            exchanges code + verifier for tokens
                                            → McpOAuthTokenStore (PVC, 0600)
            ◄── McpSignInStatus(completed) ─ normal connect; tools published
```

- **Never a credential in the UI tier.** A user interface sees an authorization URL and an
  authorization code. The code is worthless without the PKCE verifier, which never leaves the
  SDK in the agent. Tokens never cross the bus.
- **No hanging connects.** Every normal connection (startup, config reload, reconnect sweep,
  on-demand connect, tool calls) uses a *background* authorization callback. It never waits for
  a person. Instead it marks the server *sign-in required*, broadcasts that once, and fails with
  `TokenAcquisitionException(not_authenticated)`. Tool calls turn that into a non-retryable
  `auth_required` tool error that names the server and tells the model how the user signs in.
- **No network round trip before a sign-in.** If the bridge holds no tokens for a server, a
  normal connect doesn't contact it at all. It marks the server *sign-in required* straight away.
  Finding out over the network would cost the authorization server a dynamic client registration
  on every startup, config reload and on-demand connect, and servers rate-limit those (the
  scheduler allows 10 an hour per IP). Only the sign-in the user starts registers a client.
- **A revoked grant drops the connection.** When a connected server stops accepting the agent's
  tokens and refresh fails, the bridge retires the connection and withdraws its tools, keeping
  the config. Later calls then get the sign-in message without repeating the failed refresh.
- **Sign-in is started by the user.** A start request runs a dedicated connect whose callback
  publishes the authorization URL and then waits for the redirect, for at most
  `McpBridge:OAuthSignInTimeoutSeconds` (default 10 minutes). Each server has at most one sign-in
  under way, and a new one cancels the old. The sign-in uses a token-cache view that starts
  empty, so it always runs the full flow and what it stores replaces the old sign-in.
- **The redirect URI belongs to the UI.** Each start request names its own redirect URI:
  - Blazor: `{base}/mcp-oauth/callback`, which requires the app's own sign-in when that is on.
  - CLI: a loopback listener on `http://127.0.0.1:{port}/callback` (RFC 8252), or a pasted URL.

  The callback is matched by `state`, so any interface can relay it.

## What the SDK does and what the bridge does

`ModelContextProtocol` 2.2.0's `ClientOAuthOptions` does the protocol work:

- PRM and AS discovery
- scope selection (the 401's `scope`, then PRM `scopes_supported`, then the configured `scopes`)
  and `offline_access` when the AS advertises it
- DCR or CIMD
- PKCE
- `state` and `iss` validation
- the code exchange
- refresh on 401/403
- re-running the flow when refresh fails

The bridge supplies three things:

| Supplied | Where |
|---|---|
| `AuthorizationCallbackHandler`: background (fail fast) or sign-in (hand the URL out, wait for the code) | `McpBridgeService.OAuth.cs` |
| `ITokenCache`: durable, per server | `Auth/McpOAuthTokenStore.cs` |
| Client settings from `mcp.json` | `McpServerAuthConfig.OAuth` |

## Configuration

```json
"tasks-api": {
  "type": "http",
  "url": "https://tasks.example/mcp",
  "auth": {
    "oauth": {
      "clientId": "00000000-0000-0000-0000-000000000000",
      "clientSecret": "${TASKS_MCP_CLIENT_SECRET}",
      "scopes": ["api://tasks/Mcp.Tools"]
    }
  }
}
```

- `auth` takes exactly one of `profile` (a named token provider, as for Work IQ) and `oauth`.
  Any other combination is a config error, and the bridge refuses to connect the server.
- **No client settings.** `"oauth": {}` uses the authorization server's client id metadata
  documents or dynamic client registration, when it offers them.
- **Entra ID** offers neither of those, so the server's client must be pre-registered:
  - Set `clientId`.
  - Register every redirect URI the user interfaces use: the web chat's
    `https://<host>/mcp-oauth/callback` as a Web redirect, and `http://localhost` as a
    mobile/desktop redirect for the CLI's loopback port.
  - Code redemption from a Web redirect needs `clientSecret`. `${ENV}` references are expanded,
    so the secret can live in a Kubernetes Secret.
- `clientMetadataDocumentUri` and `additionalAuthorizationParameters` pass straight through to
  the SDK.
- Like every auth block, `auth.oauth` is operator policy (`HasOperatorPolicy`). `mcp_register_server`
  can't set it, and an entry with it is not the agent's to remove.

## Token store

- **Location.** `McpBridge:OAuthTokenDirectory`, default `secrets/mcp-oauth` next to `mcp.json`.
  In the pod that is `/data/agent/secrets/mcp-oauth`.
- **One file per server**, named by a hash of the normalized URL and client id. Renaming the
  entry or editing its tool filters keeps the sign-in, while pointing it elsewhere starts over.
- **Contents.** `{ "version": 1, "serverUrl", "tokens": TokenContainer }`. The SDK's container
  includes the client registration (client id and secret, auth method, authorization server), so
  a restart refreshes without re-registering.
- **Write safety.** Files are written to a temporary file and renamed into place. They are
  owner-only from creation, in an owner-only directory.
- **Unreadable files** are copied aside as `.corrupt` and treated as signed out.
- Versioned per [`schema-migrations.md`](schema-migrations.md).

## Bus contracts

All live in `RockBot.UserProxy.Abstractions` (`McpSignInMessages.cs`).

| Message | Topic | Direction |
|---|---|---|
| `McpSignInStartRequest` → `McpSignInStartResponse` | `user.mcp.signin.{agent}`, reply to the proxy's point-to-point topic | UI → agent → UI |
| `McpSignInCallback` | `user.mcp.signin.{agent}` | UI → agent |
| `McpSignInListRequest` → `McpSignInListResponse` | `user.mcp.signin.{agent}`, reply to the proxy's point-to-point topic | UI → agent → UI |
| `McpSignInStatus` | `user.response.{agent}` (broadcast) | agent → every UI |

`McpSignInStatus.State` is one of the following:

| State | Meaning |
|---|---|
| `required` | The server needs a sign-in. |
| `signed_in` | A normal connect succeeded after a sign-in was required. |
| `pending` | A sign-in is under way (list only). |
| `completed` | The sign-in finished. |
| `failed` | The sign-in failed. |
| `expired` | The sign-in was not finished in time. |
| `cancelled` | The sign-in was replaced by a newer one, or the agent stopped. |

`UserProxyService` exposes `StartMcpSignInAsync`, `SendMcpSignInCallbackAsync`,
`ListMcpSignInsAsync` and `WaitForMcpSignInAsync`. It also hands statuses to
`IUserFrontend.OnMcpSignInStatusAsync`.

## User interfaces

- **Blazor.**
  - `McpSignInBanner` lists the servers that need a sign-in. It is seeded once from the list
    request and kept current by the broadcasts.
  - "Sign in" asks for the URL and shows a "Continue to sign-in" link. It is a link rather than
    a script-opened window, which popup blockers stop.
  - `/mcp-oauth/callback` relays the redirect and tells the user to close the tab.
- **CLI.**
  - `rockbot auth mcp` lists the agent's OAuth servers.
  - `rockbot auth mcp <server>` signs in. It uses a loopback listener by default. `--port`
    fixes the listener's port, `--paste` lets the user paste the URL the browser ended on
    instead, and `--redirect-uri` sends the browser to some other relaying page (such as the web
    chat's) and just waits.

## Not covered

- **Work IQ** still uses its MSAL device-code profile. The agent365 servers are Entra-backed, so
  moving them to `auth.oauth` needs a pre-registered client id and redirect URIs. Removing the
  M365-specific UI (#441) is a follow-up once this path is proven against them.
- **One user per agent.** Tokens are per server, not per user, matching the agent's single-user
  model ([`blazor-ui.md`](../docs/blazor-ui.md)).
- **Attachments.** An `attachments` block's downloads on an OAuth server don't carry the bearer
  yet. They use the plain HTTP client with the entry's headers.
