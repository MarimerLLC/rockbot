namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Thrown when no usable access token can be produced for an MCP server — for example, when
/// the user has never signed in, or the refresh token has been revoked. Carries an actionable
/// message suitable for surfacing in a tool error back to the LLM.
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
        /// <summary>The user must sign in: they never have, or the refresh failed.</summary>
        public const string NotAuthenticated = "not_authenticated";
    }
}
