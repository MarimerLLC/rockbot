namespace RockBot.A2A.Gateway;

/// <summary>
/// Configuration for the A2A gateway's published agent card.
/// Bound from the "Gateway" configuration section.
/// </summary>
public sealed class GatewayOptions
{
    public string AgentName { get; set; } = "RockBot";
    public string? Description { get; set; }
    public string? Version { get; set; }
    public List<GatewaySkillConfig> Skills { get; set; } = [];
}

public sealed class GatewaySkillConfig
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}
