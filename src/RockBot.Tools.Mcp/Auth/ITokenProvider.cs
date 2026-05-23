namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Source of bearer access tokens for an authenticated MCP server. Implementations
/// are responsible for caching, refreshing, and (when appropriate) signalling that
/// re-consent is required out of band.
/// </summary>
public interface ITokenProvider
{
    /// <summary>
    /// Returns a usable access token. When <paramref name="forceRefresh"/> is true,
    /// any cached access token must be discarded and a fresh one acquired (typically
    /// via refresh-token rotation). Implementations may still cache the resulting
    /// token for subsequent non-forced calls.
    /// </summary>
    /// <exception cref="TokenAcquisitionException">
    /// Thrown when a token cannot be obtained — for example, because the refresh
    /// token has been revoked or the user has not yet completed initial consent.
    /// </exception>
    Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken);
}
