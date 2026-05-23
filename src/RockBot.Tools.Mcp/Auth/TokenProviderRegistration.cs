namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Binds a profile name to a token provider for registration in DI. The
/// registry consumes these to build its profile lookup.
/// </summary>
public sealed record TokenProviderRegistration(string Profile, ITokenProvider Provider);
