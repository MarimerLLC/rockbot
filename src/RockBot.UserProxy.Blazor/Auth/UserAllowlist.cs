using System.Security.Claims;

namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>
/// Decides whether a signed-in identity is allowed in, by matching its email claim against the
/// configured allowlist. Deliberately a pure class with no dependency on ASP.NET plumbing beyond
/// <see cref="ClaimsPrincipal"/>, so every rule below is directly testable.
/// </summary>
public sealed class UserAllowlist
{
    private readonly HashSet<string> _emails;
    private readonly HashSet<string> _domains;

    /// <summary>Builds an allowlist from the configured emails and domains.</summary>
    public UserAllowlist(IEnumerable<string>? allowedEmails, IEnumerable<string>? allowedDomains)
    {
        _emails = Normalize(allowedEmails);
        _domains = Normalize(allowedDomains, trimLeadingAt: true);
    }

    /// <summary>Convenience overload binding directly to <see cref="AuthOptions"/>.</summary>
    public UserAllowlist(AuthOptions options)
        : this(options.AllowedEmails, options.AllowedDomains)
    {
    }

    /// <summary>
    /// True when the list has no entries at all. This is never treated as "allow everyone" —
    /// startup validation rejects the configuration before the app can serve a request.
    /// </summary>
    public bool IsEmpty => _emails.Count == 0 && _domains.Count == 0;

    /// <summary>
    /// Evaluates a signed-in principal. Returns false for an anonymous principal, one with no email
    /// claim, or one whose provider says the address is unverified — an unverified address proves
    /// nothing about who controls it, so it must not satisfy a domain rule.
    /// </summary>
    public bool IsAllowed(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
            return false;

        if (!IsEmailVerified(principal))
            return false;

        return IsAllowed(GetEmail(principal));
    }

    /// <summary>
    /// Evaluates a bare email address. An empty allowlist denies everyone — the safe reading of
    /// "nobody is listed" is "nobody gets in".
    /// </summary>
    public bool IsAllowed(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        var normalized = email.Trim().ToLowerInvariant();

        if (_emails.Contains(normalized))
            return true;

        // Exact match on the domain after the FINAL '@' — a suffix comparison would let
        // evil-example.com satisfy an example.com rule, and an address may legally contain
        // a quoted '@' in its local part.
        var at = normalized.LastIndexOf('@');
        if (at <= 0 || at == normalized.Length - 1)
            return false;

        return _domains.Contains(normalized[(at + 1)..]);
    }

    /// <summary>Reads the email claim, preferring the standard claim type over the short OIDC name.</summary>
    public static string? GetEmail(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ClaimTypes.Email)?.Value
        ?? principal?.FindFirst("email")?.Value;

    /// <summary>
    /// True unless the provider explicitly says the address is unverified. Absent means "the
    /// provider does not publish this", which is not evidence of a problem; a literal "false" is.
    /// Google emits <c>email_verified</c>, which the ASP.NET Core handler surfaces under that name.
    /// </summary>
    public static bool IsEmailVerified(ClaimsPrincipal? principal)
    {
        var claim = principal?.FindFirst("email_verified")
            ?? principal?.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailverified");

        if (claim is null)
            return true;

        return !bool.TryParse(claim.Value, out var verified) || verified;
    }

    private static HashSet<string> Normalize(IEnumerable<string>? values, bool trimLeadingAt = false)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var normalized = value.Trim().ToLowerInvariant();
            if (trimLeadingAt)
                normalized = normalized.TrimStart('@');

            if (normalized.Length > 0)
                set.Add(normalized);
        }
        return set;
    }
}
