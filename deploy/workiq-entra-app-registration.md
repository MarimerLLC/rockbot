# Microsoft Entra ID app registration request

This document describes the **Entra ID app registration** we need created to
let RockBot — an internal application — sign users into Microsoft 365 (Work IQ)
via the device-code OAuth flow. Hand this to your tenant administrator.

## What we're asking for

A single **public-client** app registration in the tenant, with delegated
permissions for Microsoft 365 Work IQ. **No client secrets or certificates are
involved.** The app cannot act on its own — every call is made on behalf of a
specific user who has signed in interactively.

## Why this shape and not something else

- **Public client (not confidential / web app)** — the application uses the
  OAuth 2.0 *device-code* flow, which is the supported pattern for headless
  apps where the user signs in on a separate device. Public client is required
  for this flow; MSAL refuses to run device-code against a confidential
  registration.
- **Single tenant** — the app is internal to this organization. Multi-tenant
  adds publisher-verification overhead with zero benefit.
- **No redirect URI** — device-code does not redirect anywhere. The user is
  shown a short code and a URL (`https://microsoft.com/devicelogin`) which
  they open in any browser.
- **Delegated permissions only** — every call requires a token issued to a
  specific user who holds a Microsoft 365 Copilot license. We do not need (and
  do not want) application/app-only access.
- **No client secret / certificate** — public-client device-code does not use
  one. Adding one would not improve security here; it would just be unused
  credential material.

## Step-by-step in the Azure portal

The administrator with permission to create app registrations and grant admin
consent should perform these steps. Total time: ~5 minutes.

### 1. Create the registration

1. Sign in to <https://portal.azure.com> as a tenant admin.
2. Navigate to **Microsoft Entra ID** → **App registrations** → **New
   registration**.
3. Fill in the form:
   - **Name**: `RockBot WorkIQ` (or whatever naming convention the org uses;
     this string is visible in user consent prompts).
   - **Supported account types**: **Accounts in this organizational directory
     only (Single tenant)**.
   - **Redirect URI**: **leave blank**. Device-code does not redirect.
4. Click **Register**.
5. From the **Overview** page, copy two GUIDs to share back with the requester:
   - **Application (client) ID**
   - **Directory (tenant) ID**

### 2. Enable public-client flows

This is what lets MSAL run the device-code flow against this registration.

1. **Authentication** (left navigation).
2. Scroll to the bottom — **Advanced settings**.
3. **Allow public client flows** → toggle to **Yes**.
4. Click **Save** at the top.

### 3. Add Work IQ delegated permissions

Work IQ permissions live under Microsoft Graph as delegated scopes. The exact
permission names are part of the Work IQ preview surface and have shifted
once or twice since launch — whatever the picker shows in step 3.2 is
authoritative; please copy those names verbatim back to the requester.

1. **API permissions** (left navigation) → **Add a permission**.
2. **Microsoft Graph** → **Delegated permissions**.
3. In the search box, type `WorkIQ`. Add **at minimum** the following two
   permissions:
   - `WorkIQ-MailServer` (read user's mail context via Work IQ)
   - `WorkIQ-Calendar` (read user's calendar context via Work IQ)

   If additional Work IQ servers have been requested (Teams, SharePoint,
   OneDrive, Copilot Search, etc.), add the corresponding `WorkIQ-*`
   permissions at the same time.
4. Click **Add permissions**.
5. **Grant admin consent for &lt;tenant&gt;** — this is a button at the top of
   the API permissions list. **This step is required.** Without admin consent,
   the application's silent token refresh will fail with `AADSTS65001` on
   every call after the first hour, and the application will be unusable in
   practice.

### 4. Confirm the registration

The **API permissions** page should now show each `WorkIQ-*` permission as
**Granted for &lt;tenant&gt;** with a green checkmark under the **Status**
column. If any permission shows **Not granted for &lt;tenant&gt;** or a yellow
warning, return to step 3.5.

The **Authentication** page should show **Allow public client flows: Yes**
under Advanced settings.

## What to send back to the requester

1. **Tenant ID** (GUID from step 1.5)
2. **Client ID** (GUID from step 1.5)
3. **Exact scope strings** as they appeared in the permission picker, copied
   verbatim. Typical shape: `WorkIQ-MailServer/.default`,
   `WorkIQ-Calendar/.default` — but please confirm against what Entra showed
   in step 3.3; the trailing `/.default` may or may not be required depending
   on the current preview convention.

Example reply:

```
Tenant ID: 12345678-1234-1234-1234-123456789abc
Client ID: 87654321-4321-4321-4321-cba987654321
Scopes:
  WorkIQ-MailServer/.default
  WorkIQ-Calendar/.default
```

## What we are NOT asking for

For clarity, the application does **not** need any of these — please do not
add them, since unused credential material increases the attack surface
without benefit:

- ❌ A client secret
- ❌ A certificate
- ❌ Federated credentials
- ❌ A redirect URI
- ❌ Custom app roles or exposed APIs
- ❌ App-only / application permissions (only delegated permissions)
- ❌ Owner assignments beyond the default

## What happens after the registration is in place

The application uses the tenant ID, client ID, and scope strings in its own
configuration. The first time a user clicks "Connect M365" in the
application, MSAL displays a short code and asks them to open
`https://microsoft.com/devicelogin` in a browser. The user signs in with
their normal organizational credentials, approves the consent prompt
(showing the `WorkIQ-*` scopes), and the application receives a token. The
token refreshes silently for ~90 days; after that, the user is prompted to
sign in again.

The application is unable to access any user's data without that user
completing the sign-in. There is no admin-on-behalf-of capability, no
service-account-like access, and no way for the application to act outside
of a signed-in user session.

## Operational notes

- **Token revocation**: the administrator can revoke the user's session at
  any time via **Microsoft Entra ID** → **Users** → &lt;user&gt; → **Sign-ins**
  → **Revoke sessions**. The application handles this gracefully — the next
  attempted call will fail with an authentication error and the user will
  be prompted to sign in again.
- **Removing a permission**: if a Work IQ permission needs to be removed
  later, doing so via **API permissions** → **Revoke admin consent** is
  sufficient. The application surfaces a clear error to the user on the
  next call.
- **License requirement**: each user who signs in via this application must
  hold a **Microsoft 365 Copilot license** in addition to their normal M365
  license. Work IQ rejects token requests for users without a Copilot license.
- **Audit trail**: every sign-in is visible under **Microsoft Entra ID** →
  **Monitoring** → **Sign-in logs**, filtered to the `RockBot WorkIQ`
  application name.

## Questions

If anything is unclear or your security review needs a different shape (for
example, app-only access is not supported by Work IQ and is therefore not
on the table, but other constraints can be discussed), please reach out
before completing the registration so we can adjust.
