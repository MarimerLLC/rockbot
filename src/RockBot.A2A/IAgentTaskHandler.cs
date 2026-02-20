namespace RockBot.A2A;

/// <summary>
/// User-implemented handler for agent-to-agent task requests.
/// The framework dispatches <see cref="AgentTaskRequest"/> from the bus
/// and delegates to this handler for application logic.
/// </summary>
public interface IAgentTaskHandler
{
    Task<AgentTaskResult> HandleTaskAsync(
        AgentTaskRequest request, AgentTaskContext context);
}
