namespace RockBot.A2A.Gateway.Auth;

/// <summary>
/// Maps an API key to a caller identity.
/// Configured in the "ApiKeys" section: { "the-key-value": { "AgentId": "...", "DisplayName": "..." } }.
/// </summary>
public sealed class ApiKeyEntry
{
    public required string AgentId { get; set; }
    public required string DisplayName { get; set; }
}
