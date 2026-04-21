namespace RockBot.A2A;

/// <summary>
/// Handler for a single named skill on an agent. Each implementation declares
/// the <see cref="AgentSkill"/> it serves and implements the logic for exactly
/// that skill. The framework's <see cref="SkillDispatchingTaskHandler"/>
/// resolves all registered skill handlers and dispatches inbound
/// <see cref="AgentTaskRequest"/>s to the one whose <see cref="Skill"/>.Id
/// matches <see cref="AgentTaskRequest.Skill"/> (case-insensitive).
/// </summary>
/// <remarks>
/// Agents with a single custom dispatch model (payload-based, caller-based,
/// etc.) can continue to register <see cref="IAgentTaskHandler"/> directly
/// and ignore this interface. Registering both patterns on the same agent
/// produces a startup error — see <see cref="A2ASkillHandlerExtensions"/>.
/// </remarks>
public interface IAgentSkillHandler
{
    /// <summary>
    /// Metadata for the skill this handler serves. Used for dispatch
    /// (matching <see cref="AgentTaskRequest.Skill"/> against
    /// <see cref="AgentSkill.Id"/>) and for auto-populating the agent's
    /// advertised <see cref="AgentCard.Skills"/>.
    /// </summary>
    AgentSkill Skill { get; }

    /// <summary>
    /// Execute the request. Called only when <see cref="AgentTaskRequest.Skill"/>
    /// matches <see cref="Skill"/>.Id.
    /// </summary>
    Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, AgentTaskContext context);
}
