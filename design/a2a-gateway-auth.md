# A2A Gateway Authentication & Claims Propagation

## Overview

The A2A HTTP gateway (`RockBot.A2A.Gateway`) lets any A2A v1 agent discover and call
RockBot over HTTP JSON-RPC, bridging requests onto RabbitMQ. It authenticates inbound
HTTP callers and propagates the verified caller identity to the agent so the agent's
trust model sees the real caller — not the gateway.

Two authentication schemes are supported and may be enabled together; a request
satisfying **either** is accepted:

| Scheme | Header | Caller identity | Agent-side verification |
|--------|--------|-----------------|-------------------------|
| API key | `X-Api-Key` | Looked up from configured key → `AgentId` | Name-based, `IsSelfAsserted = true` |
| JWT / Bearer | `Authorization: Bearer <jwt>` | JWT `sub` claim | Claims-based, `IsSelfAsserted = false` |

## API key authentication

`ApiKeyAuthenticationHandler` validates `X-Api-Key` against the `ApiKeys` config section:

```json
"ApiKeys": {
  "the-secret-key-value": { "AgentId": "peer-agent", "DisplayName": "Peer Agent" }
}
```

The matched `AgentId` becomes the envelope `Source`. The agent verifies it by name only,
so these identities are **self-asserted** — adequate for trusted internal peers, not for
zero-trust callers.

## JWT / Bearer authentication (generic OIDC)

Bearer auth uses the standard `Microsoft.AspNetCore.Authentication.JwtBearer` handler with
generic OIDC: configure an `Authority` (issuer) and `Audience`; signing keys are discovered
automatically from `{Authority}/.well-known/openid-configuration`. Any compliant IdP works
(Azure AD / Entra, Auth0, Keycloak, Okta, …).

```json
"Jwt": {
  "Authority": "https://login.example.com/",
  "Audience": "api://rockbot-a2a",
  "RequireHttpsMetadata": true
}
```

- **Authority** — required to enable Bearer auth. Empty/unset → Bearer scheme is not
  registered and the gateway accepts API keys only.
- **Audience** — when set, tokens whose `aud` does not match are rejected. When empty,
  audience validation is disabled.
- **RequireHttpsMetadata** — defaults `true`; set `false` only for local dev against an
  http authority.

The JWT `sub` claim becomes the envelope `Source` (it maps to `ClaimTypes.NameIdentifier`).

## Agent-card advertisement

`GET /.well-known/agent-card.json` advertises the enabled schemes so A2A clients know how to
authenticate. `apiKey` is always present; when JWT is enabled the card additionally exposes a
`bearer` (`HttpAuthSecurityScheme`, scheme `bearer`, format `JWT`) and an `openId`
(`OpenIdConnectSecurityScheme` pointing at the authority's discovery document) scheme.

`SecurityRequirements` lists `apiKey` and `bearer` as **separate** requirement entries, which
in A2A/OpenAPI semantics means a caller satisfies the requirement with **either** scheme
(requirements are OR-ed; schemes within one requirement are AND-ed).

## End-to-end claims propagation

For Bearer-authenticated callers the gateway forwards the verified claims to the agent so the
agent can independently treat the identity as verified rather than trusting a bare string.

1. After the gateway validates the JWT, `RockBotBridgeHandler` extracts a small claim set
   (`sub`, `name`, `iss`, `scope`) from the authenticated principal and JSON-encodes it into
   the `rb-auth-claims` envelope header (`WellKnownHeaders.AuthClaims`). The `rb-` prefix
   ensures the header round-trips through the RabbitMQ AMQP header mapping.
   - API-key callers get **no** `rb-auth-claims` header (they stay name-based).
2. On the agent, `ClaimsForwardingAgentIdentityVerifier` (the default `IAgentIdentityVerifier`):
   - If `rb-auth-claims` is present → builds a `VerifiedAgentIdentity` with
     `IsSelfAsserted = false`, `Issuer` = the IdP `iss`, `AgentId` = `sub`, and `Claims` =
     the forwarded set.
   - Otherwise → delegates to `NameBasedAgentIdentityVerifier` (`IsSelfAsserted = true`).

This is **claims propagation**, not token re-validation: the trust boundary is the gateway,
and RabbitMQ is an internal, trusted transport. Forwarding the raw token for independent
agent-side re-validation against JWKS is a deliberate non-goal of this design (it would
require every agent process to have IdP/JWKS network access and config).

## Registration

```csharp
// Gateway host
builder.Services.AddA2AApiKeyAuthentication()
    .AddA2AJwtBearerAuthentication(jwtOptions);   // no-ops when Authority is unset

// Agent host — the claims-forwarding verifier is registered by AddA2A() automatically;
// override IAgentIdentityVerifier via DI for custom (e.g. registry-backed) verification.
```
