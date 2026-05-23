namespace RockBot.Agent.McpBridge;

/// <summary>
/// Auth configuration for an MCP server. References a named token provider
/// profile (resolved by <c>ITokenProviderRegistry</c>) so refresh tokens and
/// client secrets never appear in <c>mcp.json</c>.
/// </summary>
public sealed class McpServerAuthConfig
{
    /// <summary>
    /// Named auth profile. Resolved at connect time to an
    /// <c>ITokenProvider</c> registered in DI.
    /// </summary>
    public required string Profile { get; set; }
}
