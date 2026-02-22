using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Provides a skill guide for the A2A caller tools (invoke_agent, list_known_agents).
/// </summary>
public sealed class A2ACallerSkillProvider : IToolSkillProvider
{
    public string Name => "a2a";
    public string Summary => "Invoke external A2A agents by name and skill (invoke_agent, list_known_agents).";

    public string GetDocument() =>
        """
        # A2A Caller Tools Guide

        ## list_known_agents
        Returns all external agents that have announced themselves via the discovery bus.
        Optionally filter by skill ID.

        Parameters:
        - skill (optional): Filter to only agents that support this skill ID

        Returns: JSON array of { agentName, description, skills[] }

        ## invoke_agent
        Dispatch a task to an external agent by name. The task is sent asynchronously;
        the result arrives as a follow-up message in this conversation.

        Parameters:
        - agent_name (required): The name of the target agent (from list_known_agents)
        - skill (required): The skill ID to invoke on the target agent
        - message (required): The instruction or question for the agent
        - timeout_minutes (optional): How long to wait (default 5 minutes)

        Returns: task_id for tracking

        ## Usage pattern
        1. Call list_known_agents to see what agents and skills are available
        2. Call invoke_agent with the desired agent_name, skill, and message
        3. Continue the conversation; the agent's result will arrive automatically
           as a follow-up message and be incorporated into the conversation
        """;
}
