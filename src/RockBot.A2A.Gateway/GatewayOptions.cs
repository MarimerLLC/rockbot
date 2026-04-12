namespace RockBot.A2A.Gateway;

/// <summary>
/// Configuration for the A2A gateway's published agent card.
/// Bound from the "Gateway" configuration section.
/// </summary>
public sealed class GatewayOptions
{
    public string AgentName { get; set; } = "RockBot";

    /// <summary>
    /// Internal agent identity name used for RabbitMQ topic routing
    /// (<c>agent.task.{name}</c>). Must match the agent's <c>WithIdentity()</c>
    /// name. Defaults to <see cref="AgentName"/> when not set separately.
    /// </summary>
    public string? InternalAgentName { get; set; }

    /// <summary>
    /// Resolved internal name: <see cref="InternalAgentName"/> if set,
    /// otherwise <see cref="AgentName"/>.
    /// </summary>
    public string RoutingName => InternalAgentName ?? AgentName;

    public string? Description { get; set; }
    public string? Version { get; set; }
    public List<GatewaySkillConfig> Skills { get; set; } = [];

    /// <summary>File path for durable task storage (relative to AppContext.BaseDirectory). Null disables persistence.</summary>
    public string? TaskStorePath { get; set; } = "tasks.json";

    /// <summary>File path for push notification config storage (relative to AppContext.BaseDirectory). Null disables persistence.</summary>
    public string? PushNotificationConfigStorePath { get; set; } = "push-configs.json";

    /// <summary>Maximum seconds to wait for the agent to respond to a task (streaming or non-streaming).</summary>
    public int TaskTimeoutSeconds { get; set; } = 120;
}

public sealed class GatewaySkillConfig
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
}
