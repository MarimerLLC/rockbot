namespace RockBot.Host;

/// <summary>
/// Identifies an agent on the message bus.
/// </summary>
/// <param name="Name">Logical name of the agent (e.g. "echo-agent").</param>
/// <param name="InstanceId">Unique instance identifier, defaults to a new GUID.</param>
public sealed record AgentIdentity(string Name, string InstanceId = "")
{
    /// <summary>
    /// Unique instance identifier. Defaults to a new GUID if not provided.
    /// </summary>
    public string InstanceId { get; init; } = string.IsNullOrEmpty(InstanceId)
        ? Guid.NewGuid().ToString("N")
        : InstanceId;
}
