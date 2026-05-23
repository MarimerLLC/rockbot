namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Thrown by <see cref="BearerInjectionHandler"/> when an MCP server returns
/// 401 after both the initial request and the forced-refresh retry have
/// failed. Carries the parsed <see cref="WwwAuthenticateChallenge"/> so
/// upstream callers can surface the protected-resource-metadata URL and any
/// RFC 6750 error details in their own error responses.
/// </summary>
public sealed class McpAuthChallengeException : HttpRequestException
{
    public WwwAuthenticateChallenge? Challenge { get; }

    public McpAuthChallengeException(string message, WwwAuthenticateChallenge? challenge)
        : base(message)
    {
        Challenge = challenge;
    }
}
