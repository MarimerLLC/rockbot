namespace RockBot.UserProxy.AgUi;

/// <summary>
/// App auth for the AG-UI proxy (#794, phase 5), bound from <c>AgUi:Auth</c>. Off by default:
/// the proxy then has no sign-in of its own and whoever can reach it is allowed in ("network
/// mode"). On, every client endpoint needs an OIDC token as <c>Authorization: Bearer</c>, from an
/// account on the allowlist ("app mode").
/// </summary>
/// <remarks>
/// <para>
/// The section is the proxy's own, under <c>AgUi</c>, and not the Blazor UI's flat <c>Auth</c>:
/// the two proxies are deployed side by side from one chart, with different client ids and
/// possibly different allowlists, and neither may pick up the other's settings.
/// </para>
/// <para>
/// The proxy validates tokens; it never asks for one. So there is no client secret here and no
/// redirect URI: only who issues the tokens and which apps they must have been issued to.
/// </para>
/// </remarks>
public sealed class AgUiAuthOptions : UserAllowlistOptions
{
    /// <summary>Google's issuer, the default <see cref="Authority"/>.</summary>
    public const string GoogleAuthority = "https://accounts.google.com";

    /// <summary>Master switch. <c>false</c> (the default) leaves every endpoint open to whoever can reach it.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The OIDC issuer whose tokens are accepted. Its signing keys are read from
    /// <c>{Authority}/.well-known/openid-configuration</c>, so the proxy needs outbound HTTPS to
    /// it. Google by default; any OIDC provider that puts <c>email</c> in its tokens works.
    /// </summary>
    public string Authority { get; set; } = GoogleAuthority;

    /// <summary>
    /// The <c>aud</c> values accepted: the client ids of the apps allowed to call. A list because
    /// each kind of app has its own (a web app, an Android app, an iOS app). At least one is
    /// required: without it a token issued to any app at all, for any purpose, would be accepted.
    /// </summary>
    public IList<string> Audiences { get; set; } = new List<string>();

    /// <summary>
    /// Issuers accepted besides the one the authority's discovery document names. Rarely needed:
    /// Google's second spelling (<c>accounts.google.com</c>, without the scheme) is added
    /// automatically when the authority is Google.
    /// </summary>
    public IList<string> ValidIssuers { get; set; } = new List<string>();

    /// <summary>
    /// Require the token to say <c>email_verified: true</c> (the default). Stricter than the
    /// Blazor UI, which only refuses an explicit <c>false</c>: there the provider is one this
    /// build chose, here the authority is configuration, and a provider that lets a user name any
    /// address without verifying it would hand out any allowlisted identity. Turn it off only
    /// for a provider known to publish verified addresses without the claim.
    /// </summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    /// <summary>
    /// The claim that becomes <see cref="UserMessage.UserId"/>. <c>email</c> by default; see
    /// <see cref="AgUiIdentity"/> for why. Falls back to <c>sub</c>, then to
    /// <see cref="AgUiOptions.DefaultUserId"/>.
    /// </summary>
    public string UserIdClaim { get; set; } = "email";

    /// <summary>How far the proxy's clock and the issuer's may disagree about a token's lifetime.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an access ticket (<c>POST /tickets</c>) is good for. Short, because a ticket
    /// travels in a URL. See <see cref="AccessTicketService"/>.
    /// </summary>
    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The key access tickets are signed with: base64, at least 32 bytes. Empty (the default)
    /// makes a random key at startup, which is right for one replica: a restart then invalidates
    /// the tickets in flight, which live minutes anyway. Set it, from a Secret, only so that
    /// several replicas accept each other's tickets.
    /// </summary>
    public string? TicketKey { get; set; }

    /// <summary>The configured audiences, trimmed, without blanks.</summary>
    public IReadOnlyList<string> ResolveAudiences() =>
        [.. Audiences.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The issuers accepted besides the discovery document's own: the configured ones and, for
    /// Google, the spelling without a scheme, which Google's ID tokens may carry.
    /// </summary>
    public IReadOnlyList<string> ResolveExtraIssuers()
    {
        var issuers = ValidIssuers.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
        if (Uri.TryCreate(Authority?.Trim(), UriKind.Absolute, out var authority)
            && string.Equals(authority.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase))
        {
            issuers.Add("https://accounts.google.com");
            issuers.Add("accounts.google.com");
        }
        return [.. issuers.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The ticket signing key, or null to generate one. Throws when it is set and unusable.</summary>
    public byte[]? ResolveTicketKey()
    {
        if (string.IsNullOrWhiteSpace(TicketKey))
            return null;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(TicketKey.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("AgUi:Auth:TicketKey is not base64.");
        }

        return key.Length >= 32
            ? key
            : throw new InvalidOperationException("AgUi:Auth:TicketKey must be at least 32 bytes (44 base64 characters).");
    }

    /// <summary>
    /// What is wrong with the configuration: one message per problem, none when it is usable.
    /// Checked at startup, so an enabled-but-unusable configuration is a proxy that doesn't start
    /// rather than an open API.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        if (!Enabled)
            yield break;

        if (!AllowedEmails.Any(e => !string.IsNullOrWhiteSpace(e)) && !AllowedDomains.Any(d => !string.IsNullOrWhiteSpace(d)))
        {
            yield return
                "AgUi:Auth:Enabled is true but both AgUi:Auth:AllowedEmails and AgUi:Auth:AllowedDomains are empty. " +
                "An empty allowlist lets nobody in, and is never read as \"everyone at the provider\". " +
                "List at least one address or domain, or set AgUi:Auth:Enabled to false.";
        }

        if (ResolveAudiences().Count == 0)
        {
            yield return
                "AgUi:Auth:Enabled is true but AgUi:Auth:Audiences is empty. List the client id of every app allowed " +
                "to call the API. Without an audience, a token the provider issued to any other app would be accepted.";
        }

        if (!Uri.TryCreate(Authority?.Trim(), UriKind.Absolute, out var authority)
            || authority.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment))
        {
            yield return
                $"AgUi:Auth:Authority ('{Authority}') must be an absolute https URL, the token issuer's address, " +
                $"for example {GoogleAuthority}. Its signing keys are fetched from there, so plain http is refused.";
        }

        if (string.IsNullOrWhiteSpace(UserIdClaim))
            yield return "AgUi:Auth:UserIdClaim is empty. Use \"email\" (the default) or \"sub\".";

        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(10))
            yield return "AgUi:Auth:ClockSkew must be between 0 and 10 minutes.";

        if (TicketLifetime <= TimeSpan.Zero || TicketLifetime > TimeSpan.FromHours(1))
            yield return "AgUi:Auth:TicketLifetime must be more than 0 and at most 1 hour: a ticket travels in a URL.";

        string? keyProblem = null;
        try
        {
            ResolveTicketKey();
        }
        catch (InvalidOperationException ex)
        {
            keyProblem = ex.Message;
        }
        if (keyProblem is not null)
            yield return keyProblem;
    }
}

/// <summary>
/// Which browser origins may call the API, bound from <c>AgUi:Cors</c>. None (the default) sends
/// no CORS headers at all, so only same-origin pages and non-browser clients can use the API.
/// </summary>
public sealed class AgUiCorsOptions
{
    /// <summary>
    /// The origins allowed, each as a browser sends it: scheme, host and port, no path
    /// (<c>https://app.example.com</c>, <c>http://localhost:5173</c>). No wildcard.
    /// </summary>
    public IList<string> AllowedOrigins { get; set; } = new List<string>();

    /// <summary>
    /// The origins in the form a browser's <c>Origin</c> header has. Throws on an entry that
    /// isn't an origin, so a typo stops the proxy at startup instead of silently allowing nothing
    /// (or, with a wildcard, everything).
    /// </summary>
    public IReadOnlyList<string> ResolveOrigins()
    {
        var origins = new List<string>();
        foreach (var entry in AllowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var text = entry.Trim();
            if (text.Contains('*')
                || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.AbsolutePath.Trim('/').Length > 0
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException(
                    $"AgUi:Cors:AllowedOrigins has '{entry}', which is not an origin. Use scheme://host[:port] with no path, " +
                    "for example https://app.example.com. Wildcards are not supported: list each origin.");
            }

            origins.Add(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant());
        }
        return [.. origins.Distinct(StringComparer.Ordinal)];
    }
}
