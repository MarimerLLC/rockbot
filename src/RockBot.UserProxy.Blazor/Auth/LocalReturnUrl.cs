namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>
/// Validates the <c>returnUrl</c> carried through the sign-in flow.
/// </summary>
/// <remarks>
/// The value arrives on a query string an attacker can write, and it ends up in a redirect after a
/// successful sign-in — the classic open-redirect shape, made worse here because the victim has
/// just authenticated. Only a path rooted at this application is ever accepted.
/// </remarks>
public static class LocalReturnUrl
{
    /// <summary>Where a caller lands when no usable <c>returnUrl</c> was supplied.</summary>
    public const string Default = "/";

    /// <summary>
    /// Returns <paramref name="returnUrl"/> when it is a local path, and <see cref="Default"/>
    /// otherwise. Rejected: absolute URLs, protocol-relative <c>//host</c> and its backslash
    /// variants (browsers normalise <c>\</c> to <c>/</c> in authority position), and anything not
    /// starting with a single <c>/</c>.
    /// </summary>
    public static string Sanitize(string? returnUrl) => IsLocal(returnUrl) ? returnUrl! : Default;

    /// <summary>True when <paramref name="returnUrl"/> is a path within this application.</summary>
    public static bool IsLocal(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
            return false;

        // Must be rooted, and must not be an authority reference. Browsers normalise a backslash to
        // a forward slash in authority position, so "/\evil.com" is protocol-relative in practice
        // even though it does not look it — both slash characters have to count.
        if (returnUrl[0] is not '/')
            return false;

        if (returnUrl.Length > 1 && returnUrl[1] is '/' or '\\')
            return false;

        // "/:" would be parsed as a scheme by some consumers; nothing legitimate needs it.
        if (returnUrl.Length > 1 && returnUrl[1] is ':')
            return false;

        // A control character can truncate the header a redirect is written into.
        return !returnUrl.Any(char.IsControl);
    }
}
