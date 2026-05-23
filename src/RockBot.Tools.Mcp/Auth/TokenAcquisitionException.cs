namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Thrown by an <see cref="ITokenProvider"/> when no usable access token can be
/// produced — for example, when the underlying refresh token has been revoked
/// or initial consent has not yet completed. Carries an actionable message
/// suitable for surfacing in a tool error back to the LLM.
/// </summary>
public sealed class TokenAcquisitionException : Exception
{
    /// <summary>
    /// Stable code identifying the failure category. Surfaced to upstream callers
    /// so they can branch on the reason without parsing message text.
    /// </summary>
    public string Code { get; }

    public TokenAcquisitionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public TokenAcquisitionException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public static class Codes
    {
        /// <summary>The user has not yet consented (no cache loaded).</summary>
        public const string NotAuthenticated = "not_authenticated";

        /// <summary>Refresh token was rejected; user must re-consent interactively.</summary>
        public const string ReauthRequired = "reauth_required";

        /// <summary>Token provider could not reach the identity provider.</summary>
        public const string IdentityProviderUnreachable = "identity_provider_unreachable";

        /// <summary>Anything else that prevented acquisition.</summary>
        public const string Unknown = "unknown";
    }
}
