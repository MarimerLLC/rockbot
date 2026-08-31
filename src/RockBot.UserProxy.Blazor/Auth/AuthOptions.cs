namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>
/// OAuth sign-in configuration, bound from the <c>Auth</c> configuration section.
/// </summary>
/// <remarks>
/// Defaults are "off": a deployment that sets nothing behaves exactly as it did before sign-in
/// existed, gated only by whatever the network in front of it provides (a tailnet, typically).
/// </remarks>
public sealed class AuthOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Auth";

    /// <summary>Master switch. <c>false</c> (the default) leaves every route anonymous.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute external base URL of this deployment, e.g. <c>https://trees.example.com</c>.
    /// Optional, but it is the setting that removes all guessing: when set, OAuth callback URIs are
    /// built from it rather than from the incoming request, so a TLS-terminating proxy forwarding
    /// to plain http on :8080 cannot produce an <c>http://</c> redirect_uri that the provider then
    /// rejects with an opaque <c>redirect_uri_mismatch</c>.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// Cookie lifetime, sliding. Fourteen days by default — long enough that a pod restart or a
    /// chart upgrade does not sign everybody out, which is the point of persisting the key ring.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Trust <c>X-Forwarded-Proto</c> / <c>X-Forwarded-Host</c> from any source. Off by default:
    /// forwarded headers are attacker-controlled unless something in front strips them, and
    /// clearing the known-network list is only safe when the ingress is known to do that.
    /// Prefer <see cref="PublicBaseUrl"/>, which needs no trust at all.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }

    /// <summary>Exact email addresses allowed in. Compared case-insensitively.</summary>
    public IList<string> AllowedEmails { get; set; } = new List<string>();

    /// <summary>
    /// Email domains allowed in, e.g. <c>example.com</c>. Matched against the part after the final
    /// <c>@</c>, exactly — never as a suffix, so <c>evil-example.com</c> does not satisfy
    /// <c>example.com</c>.
    /// </summary>
    public IList<string> AllowedDomains { get; set; } = new List<string>();

    /// <summary>
    /// Configured identity providers, keyed by provider name (<c>Google</c> today). A provider is
    /// "enabled" when it appears here with both a client ID and secret.
    /// </summary>
    public IDictionary<string, OAuthProviderOptions> Providers { get; set; }
        = new Dictionary<string, OAuthProviderOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Throws when the configuration is enabled but unusable. Called at startup, before the app
    /// serves anything, so a misconfiguration is a failure to boot rather than an open door.
    /// </summary>
    /// <returns>One message per problem found; empty when the configuration is usable.</returns>
    public IEnumerable<string> Validate()
    {
        if (!Enabled)
            yield break;

        var configured = Providers
            .Where(p => p.Value is not null && p.Value.IsConfigured)
            .Select(p => p.Key)
            .ToList();

        if (configured.Count == 0)
        {
            yield return
                "Auth:Enabled is true but no identity provider is configured. Set " +
                "Auth:Providers:Google:ClientId and Auth:Providers:Google:ClientSecret, or set " +
                "Auth:Enabled to false. Starting without a provider would serve the UI with no way in.";
        }

        var hasEmails = AllowedEmails.Any(e => !string.IsNullOrWhiteSpace(e));
        var hasDomains = AllowedDomains.Any(d => !string.IsNullOrWhiteSpace(d));
        if (!hasEmails && !hasDomains)
        {
            yield return
                "Auth:Enabled is true but both Auth:AllowedEmails and Auth:AllowedDomains are empty. " +
                "An empty allowlist does not mean \"my users can get in\" — it means every account at " +
                "every configured provider can get in. Refusing to start rather than come up wide open.";
        }

        foreach (var unknown in Providers.Keys.Where(k => !AuthProviderRegistry.IsKnownProvider(k)))
        {
            yield return
                $"Auth:Providers:{unknown} names a provider this build does not support. " +
                $"Supported: {string.Join(", ", AuthProviderRegistry.KnownProviders)}.";
        }
    }
}

/// <summary>Client credentials for one OAuth identity provider.</summary>
public sealed class OAuthProviderOptions
{
    /// <summary>OAuth client ID issued by the provider.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>OAuth client secret issued by the provider. Belongs in a Secret, never a ConfigMap.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>A provider only counts as configured when both halves of the credential are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
