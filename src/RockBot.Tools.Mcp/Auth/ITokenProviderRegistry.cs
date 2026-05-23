namespace RockBot.Tools.Mcp.Auth;

/// <summary>
/// Lookup table for named <see cref="ITokenProvider"/> instances. MCP server
/// configurations reference a provider by profile name (e.g. <c>"workiq"</c>)
/// so credentials never appear in <c>mcp.json</c>.
/// </summary>
public interface ITokenProviderRegistry
{
    /// <summary>
    /// Returns the provider registered under <paramref name="profile"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when no provider is registered for the given profile name.
    /// </exception>
    ITokenProvider Get(string profile);
}
