# Google OAuth sign-in for the Blazor UI

This walks through creating the Google OAuth client the RockBot Blazor UI uses to
sign users in, and wiring it into a Helm or Docker Compose deployment.

Sign-in is **off by default**. A deployment that leaves `blazor.auth.enabled`
false behaves exactly as it always has: no login page, every route anonymous,
gated only by whatever network sits in front of it (a tailnet, typically).

## What this is and is not

It is a **doorman**. Everyone who gets in shares the same conversation with the
agent — the UI pins a single session and user id, and this work does not change
that. You are deciding *who gets in*, not *whose conversation this is*.

It is **not a replacement for the Tailscale path**. If the UI is already reachable
only from your tailnet and the ACL says who may reach it, you already have a gate.
This exists for deployments that want an ordinary HTTPS ingress instead.

## 1. Create the OAuth client in Google Cloud Console

Total time: ~5 minutes. You need a Google Cloud project; a new empty one is fine,
since nothing here calls a Google API.

### Configure the consent screen

1. Sign in to <https://console.cloud.google.com> and select (or create) a project.
2. Navigate to **APIs & Services** → **OAuth consent screen**.
3. **User type**:
   - **Internal** if everyone signing in is in your Google Workspace organization.
     This is the better choice when it is available — an internal app needs no
     verification and no test-user list.
   - **External** otherwise (personal `@gmail.com` accounts, mixed organizations).
     Leave it in **Testing** status and add each person as a **Test user**; an
     external app in testing is capped at 100 test users, which is far more than a
     RockBot instance needs. You do not need to submit it for verification, because
     the app requests no sensitive scopes.
4. Fill in the app name, a support email, and a developer contact email. The app
   name is what users see on the Google consent screen.
5. **Scopes**: add none. The default `openid`, `email`, and `profile` are all this
   needs, and they are granted without any scope configuration.

### Create the client

1. **APIs & Services** → **Credentials** → **Create credentials** → **OAuth client ID**.
2. **Application type**: **Web application**.
3. **Name**: anything; it is only visible in the console.
4. **Authorized redirect URIs** — this is the field that matters, and the one that
   causes essentially every failure of this setup. Add exactly:

   ```
   https://<your-host>/signin-google
   ```

   Read that literally:
   - **`https`**, not `http`. Google rejects plain http for every origin except
     `http://localhost`. See "Local development" below.
   - `/signin-google` exactly — that is the callback path the app registers.
   - No trailing slash. Google matches the string, not the URL semantics.
   - The host must be the address **the browser** uses, not an in-cluster service
     name.

   You can list several URIs on one client (a staging host and a production host,
   say). Adding one later takes effect within minutes.
5. **Create**, then copy the **Client ID** and **Client secret**.

## 2. Wire it into Helm

```yaml
blazor:
  auth:
    enabled: true

    # The external address, ending without a slash. Set this — see "Behind a proxy".
    publicBaseUrl: "https://rockbot.example.com"

    # WHO GETS IN. At least one entry is required across the two lists.
    allowedEmails:
      - someone@example.com
    allowedDomains:
      - example.com

    google:
      clientId: "1234567890-abcdef.apps.googleusercontent.com"

  # A way in that is not the tailnet. Mutually exclusive with tailscale.ingress.
  ingress:
    enabled: true
    className: nginx
    host: rockbot.example.com
    annotations:
      cert-manager.io/cluster-issuer: letsencrypt-prod
    tls:
      enabled: true
      secretName: rockbot-blazor-tls

secrets:
  auth:
    google:
      clientSecret: "GOCSPX-..."
```

`publicBaseUrl` plus `/signin-google` must equal the redirect URI you registered,
character for character.

### The allowlist is not optional

`allowedEmails` and `allowedDomains` cannot both be empty. The chart refuses to
render and the app refuses to start.

This is deliberate and it is the most important rule here. "Sign in with Google"
with nothing listed does not mean "the people I expect can get in" — it means
**every Google account in existence** can open a full agent session. An empty
allowlist is a misconfiguration, never a default, so it fails loudly instead of
coming up wide open.

Matching rules:

- `allowedEmails` compares the whole address, case-insensitively.
- `allowedDomains` compares the part after the final `@`, **exactly**. A suffix
  match would let `evil-example.com` satisfy an `example.com` rule.
- An address Google reports as unverified never matches, whichever list it is on.

Removing someone takes effect on their next request, and within 30 minutes on a
browser tab they already have open.

## 3. Behind a reverse proxy

Inside the cluster the app listens on plain http on `:8080`. If it built the OAuth
callback from the incoming request it would produce `http://…/signin-google`, which
Google rejects with `redirect_uri_mismatch` and no explanation.

**Set `blazor.auth.publicBaseUrl`.** With it set, every absolute URL the app builds
— the callback above all — comes from that value, and no forwarded header has to be
trusted for anything. This is the setting that removes the guessing.

`blazor.auth.trustForwardedHeaders` exists for the case where `publicBaseUrl` cannot
be pinned (several hostnames on one deployment). It makes the app trust
`X-Forwarded-Proto` and `X-Forwarded-Host` from any source, which is only safe if
your ingress strips incoming copies of those headers. Prefer `publicBaseUrl`.

## 4. Local development with Docker Compose

`http://localhost` is the single origin Google permits over plain http, so the
compose stack can exercise the real flow with no TLS.

Register a **second** redirect URI on the same client (or a separate client):

```
http://localhost:8080/signin-google
```

Then in `deploy/docker-compose/.env`:

```dotenv
BLAZOR_AUTH_ENABLED=true
BLAZOR_AUTH_PUBLIC_BASE_URL=http://localhost:8080
BLAZOR_AUTH_GOOGLE_CLIENT_ID=1234567890-abcdef.apps.googleusercontent.com
BLAZOR_AUTH_GOOGLE_CLIENT_SECRET=GOCSPX-...
BLAZOR_AUTH_ALLOWED_EMAILS=you@example.com
```

```bash
docker compose -f deploy/docker-compose/docker-compose.yml up -d --build blazor
```

## 5. Verify

| Check | Expected |
|---|---|
| Visit `/` signed out | Redirected to `/login` |
| Sign in with an allowlisted account | Chat page loads, "Sign out" appears in the header |
| Sign in with an account that is not allowlisted | `/access-denied`, naming the account, with a sign-out button |
| `GET /attachments?file=x` signed out | Not 200 — redirected to `/login` |
| `GET /healthz` | 200, signed in or not |
| Restart the container, reload | Still signed in |
| Close the browser, reopen | Still signed in |

The last two are the point of the persistent key ring and the persistent cookie
respectively; either one alone gets you neither.

## Troubleshooting

**`redirect_uri_mismatch`.** The URI Google received is not one you registered. It
is printed in the error page's detail on Google's side — compare it character for
character with the console entry. Almost always: `http` instead of `https` (set
`publicBaseUrl`), a trailing slash, or an internal hostname leaking through.

**Signed straight back out after a restart.** The data-protection key ring is not
persisting. Confirm `/data/blazor/keys` is mounted read-write and contains
`key-*.xml`; the app fails to start rather than falling back to in-memory keys, so a
running pod with no key files means the path is not what you think it is.

**Everyone lands on `/access-denied`.** The allowlist does not match the address
Google returns. The Blazor pod logs the address on every denial:

```bash
kubectl logs -n rockbot -l app=rockbot-blazor | grep "not on the allowlist"
```

**Blank page or unstyled login page.** Not an auth problem — check that the
Blazor framework static files published correctly (`blazor.web.js` must not 404).

## Adding another provider

Everything above is written against a list. Microsoft or GitHub would be a package
reference, an entry in `AuthProviderRegistry.Descriptors`, and one case in the
registration switch in `AuthSetup.AddConfiguredProviders`. No provider name is
hardcoded anywhere else — the login page, the challenge endpoint, and the
remembered-provider key all read the registry.
