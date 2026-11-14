# Google sign-in for clients of the AG-UI proxy

This is the Google Cloud Console side of [app auth](../docs/agui.md#app-auth) for the AG-UI proxy
(`RockBot.UserProxy.AgUi`). Read [`blazor-oauth-setup.md`](blazor-oauth-setup.md) first for the
parts that are the same: the project, the consent screen ("Google Auth Platform"), Internal versus
External, and test users. This page covers what differs.

App auth is **off by default**. A proxy that leaves `agui.auth.enabled` false asks nobody who they
are and belongs on a private network.

## What is different from the Blazor UI

| | Blazor UI | AG-UI proxy |
|---|---|---|
| Who signs the user in | The server, with a redirect to Google and back | The client app, with Google's own library |
| What the server gets | An authorization code, which it exchanges | An ID token on every request (`Authorization: Bearer`) |
| Client secret | Needed (`secrets.auth.google.clientSecret`) | **None.** The proxy only checks tokens' signatures against Google's public keys. |
| What to register on the client | An *Authorized redirect URI* (`https://<host>/signin-google`) | *Authorized JavaScript origins* for a web app; a package name or bundle id for a phone app. **No redirect URI.** |
| What the server is told | The client id, to ask with | The client ids, to accept: `agui.auth.audiences` |
| Session | A cookie, 14 days | None. The token lasts an hour and the client renews it. |

Use **separate OAuth clients** from the Blazor UI's. An ID token is issued *to* a client id (its
`aud`), and the proxy accepts the audiences you list and no others. Listing the Blazor UI's client
id would let a token from that sign-in call the API.

## 1. Create a client for each app

**Google Auth Platform** → **Clients** → **Create client**, once for each kind of app.

### A browser or WebAssembly app

1. **Application type**: **Web application**.
2. **Authorized JavaScript origins**: the origin the app is served from, exactly as the browser
   shows it, with no path and no trailing slash:

   ```
   https://app.example.com
   ```

   Add `http://localhost:<port>` for local development; Google allows plain http for `localhost`
   only.
3. **Authorized redirect URIs**: leave empty. Google Identity Services hands the token to the page
   that asked; nothing is redirected.
4. **Create**, and copy the **Client ID**. Ignore the client secret: nothing uses it.

The same origin goes in `agui.cors.allowedOrigins`, unless the app is served from the proxy's own
address.

### An Android or iOS app

1. **Application type**: **Android** (package name and signing-certificate SHA-1) or **iOS**
   (bundle id).
2. To get an ID token on Android you also need a **Web application** client: the app asks for a
   token *for* that client (`setServerClientId`), and its client id is the token's `aud`. It can
   be the web app's client, or one made only for this.

Whichever client id ends up as the `aud` of the tokens the app sends is the one to list.

## 2. Wire it into Helm

```yaml
agui:
  enabled: true
  auth:
    enabled: true
    # The aud of the tokens to accept: one client id per app.
    audiences:
      - 1234567890-abcdef.apps.googleusercontent.com
    # WHO GETS IN. At least one entry is required across the two lists.
    allowedEmails:
      - someone@example.com
    allowedDomains: []
  cors:
    allowedOrigins:
      - https://app.example.com
```

There is nothing to put in `secrets`. The chart refuses to render, and the proxy refuses to
start, with `auth.enabled` and an empty allowlist or no audience.

The allowlist follows the Blazor UI's rules (whole address, case-insensitive; domain matched
exactly after the final `@`), with one difference: the token must say `email_verified: true`.
Google always says so for an address it has verified.

The pod fetches Google's signing keys, so it needs **outbound HTTPS** to `accounts.google.com`
and `www.googleapis.com`.

## 3. Verify

Get an ID token (in a browser app, log the `credential` that Google Identity Services returns; it
is the three dot-separated parts), then:

| Check | Expected |
|---|---|
| `curl https://<host>/healthz` | `200`, with or without a token |
| `curl https://<host>/agent` | `401`, `WWW-Authenticate: Bearer` |
| `curl https://<host>/agent -H "Authorization: Bearer $TOKEN"` with an allowed account | `200` |
| The same with an account that is not on the allowlist | `403`; the pod's log names the address |
| The same an hour later | `401`, `error="invalid_token"`, "The token has expired." |
| From the web app's origin, in the browser's network tab | The preflight (`OPTIONS`) answers `204` with `Access-Control-Allow-Origin` |

## Troubleshooting

**Every token is `401`.** The pod's log says why (`Refused a bearer token: …`):

```bash
kubectl logs -n rockbot -l app=rockbot-agui | grep "Refused a bearer token"
```

- *The audience … is invalid*: the token's `aud` isn't in `agui.auth.audiences`. Decode the token
  (it is base64) and compare. On Android it is the *web* client's id, not the Android client's.
- *Unable to obtain configuration* or *signature key was not found*: the pod can't reach
  `accounts.google.com`. Check egress.
- The app sent Google's **access token** (`ya29.…`). That isn't a JWT; send the **ID token**.

**`403` for an account that should get in.** The log has the address Google sent
(`Refused someone@example.com: …`). Compare it with the allowlist.

**The browser reports a CORS error and the proxy's log shows nothing.** The page's origin isn't in
`agui.cors.allowedOrigins`, character for character: scheme, host and port.

**Google's button says "origin_mismatch" or the popup closes at once.** The page's origin isn't
among the client's *Authorized JavaScript origins*. A change there can take a few minutes.

## Another provider

Nothing is specific to Google except the default. Set `agui.auth.authority` to any OIDC issuer
(`https://login.microsoftonline.com/<tenant>/v2.0`, a Keycloak realm) and `agui.auth.audiences`
to the client ids it issues tokens for. The tokens need `email` and `email_verified` claims; for
a provider that verifies addresses without saying so, set the `AgUi__Auth__RequireVerifiedEmail`
environment variable to `false` on the deployment, and only then.
