using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Parsed representation of an HTTP <c>WWW-Authenticate</c> response header.
/// The MCP authorization spec uses this header to advertise authentication
/// requirements via the <see cref="ResourceMetadata"/> URL (RFC 9728).
/// RFC 6750's <c>error</c>, <c>error_description</c>, and <c>realm</c> are
/// also exposed for diagnostics.
/// </summary>
/// <remarks>
/// The parser is tolerant: malformed input produces <c>false</c> from
/// <see cref="TryParse"/> rather than throwing. The intent is to never let
/// a misbehaving server crash the bridge's auth path.
/// </remarks>
public sealed class WwwAuthenticateChallenge
{
    private static readonly Regex ParamPattern = new(
        """(?<key>[a-zA-Z0-9_-]+)\s*=\s*(?:"(?<qval>(?:[^"\\]|\\.)*)"|(?<val>[^\s,]+))""",
        RegexOptions.Compiled);

    /// <summary>Authentication scheme (e.g. <c>Bearer</c>, <c>DPoP</c>).</summary>
    public required string Scheme { get; init; }

    /// <summary>
    /// The <c>resource_metadata</c> parameter — RFC 9728 protected resource metadata URL.
    /// The MCP OAuth spec uses this to advertise where clients can discover auth
    /// requirements. May be null when the server has not adopted that part of the spec.
    /// </summary>
    public string? ResourceMetadata { get; init; }

    /// <summary>RFC 6750 <c>realm</c> parameter, if present.</summary>
    public string? Realm { get; init; }

    /// <summary>RFC 6750 <c>scope</c> parameter (space-separated scope list), if present.</summary>
    public string? Scope { get; init; }

    /// <summary>RFC 6750 <c>error</c> parameter (e.g. <c>invalid_token</c>), if present.</summary>
    public string? Error { get; init; }

    /// <summary>RFC 6750 <c>error_description</c> parameter, if present.</summary>
    public string? ErrorDescription { get; init; }

    /// <summary>RFC 6750 <c>error_uri</c> parameter, if present.</summary>
    public string? ErrorUri { get; init; }

    /// <summary>The original header value, preserved for diagnostics and logging.</summary>
    public required string RawValue { get; init; }

    /// <summary>
    /// Attempts to parse a <c>WWW-Authenticate</c> header value. Only the first
    /// challenge is parsed; multi-challenge headers are rare in MCP usage and
    /// the spec only requires Bearer.
    /// </summary>
    public static bool TryParse(string? headerValue, [NotNullWhen(true)] out WwwAuthenticateChallenge? challenge)
    {
        challenge = null;
        if (string.IsNullOrWhiteSpace(headerValue))
            return false;

        var trimmed = headerValue.Trim();
        var schemeEnd = trimmed.IndexOf(' ');
        string scheme;
        string paramSection;

        if (schemeEnd < 0)
        {
            // Scheme alone with no parameters — valid for e.g. "Bearer"
            scheme = trimmed;
            paramSection = string.Empty;
        }
        else
        {
            scheme = trimmed[..schemeEnd];
            paramSection = trimmed[(schemeEnd + 1)..];
        }

        if (string.IsNullOrEmpty(scheme))
            return false;

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ParamPattern.Matches(paramSection))
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["qval"].Success
                ? UnescapeQuoted(match.Groups["qval"].Value)
                : match.Groups["val"].Value;
            parameters[key] = value;
        }

        challenge = new WwwAuthenticateChallenge
        {
            Scheme = scheme,
            RawValue = headerValue,
            ResourceMetadata = parameters.GetValueOrDefault("resource_metadata"),
            Realm = parameters.GetValueOrDefault("realm"),
            Scope = parameters.GetValueOrDefault("scope"),
            Error = parameters.GetValueOrDefault("error"),
            ErrorDescription = parameters.GetValueOrDefault("error_description"),
            ErrorUri = parameters.GetValueOrDefault("error_uri")
        };
        return true;
    }

    private static string UnescapeQuoted(string value) =>
        Regex.Replace(value, @"\\(.)", "$1");
}
