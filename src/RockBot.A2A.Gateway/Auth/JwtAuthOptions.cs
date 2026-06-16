namespace RockBot.A2A.Gateway.Auth;

/// <summary>
/// Configuration for the gateway's JWT/Bearer authentication scheme, using generic OIDC.
/// Bound from the "Jwt" configuration section. When <see cref="Authority"/> is empty the
/// Bearer scheme is not registered and the gateway accepts API-key auth only.
/// </summary>
public sealed class JwtAuthOptions
{
    /// <summary>
    /// OIDC authority (issuer) base URL. The handler discovers signing keys via
    /// <c>{Authority}/.well-known/openid-configuration</c>. Required to enable Bearer auth.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Expected token audience. When set, tokens whose <c>aud</c> claim does not match are rejected.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Whether HTTPS metadata is required when contacting the authority. Defaults to true;
    /// set false only for local development against an http authority.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// True when a non-empty <see cref="Authority"/> is configured (i.e. Bearer auth is enabled).
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(Authority);
}
