using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Provides a skill guide for the A2A caller tools (invoke_agent, list_known_agents).
/// </summary>
public sealed class A2ACallerSkillProvider : IToolSkillProvider
{
    public string Name => "a2a";
    public string Summary => "Invoke external A2A agents by name and skill (list_known_agents, get_agent_details, invoke_agent).";

    public string GetDocument() =>
        """
        # A2A Caller Tools Guide

        ## list_known_agents
        Returns a compact list of all known external agents: name, one-sentence summary,
        last-seen time, and skill IDs. The summary is LLM-generated from the agent's card
        when the agent is first discovered.
        Use this to identify which agent and skill to call.

        Parameters:
        - skill (optional): Filter to only agents that support this skill ID

        Returns: JSON array of { agentName, summary, lastSeen, skills[{ id }] }

        ## get_agent_details
        Returns the full agent card for a named agent: complete skill metadata (name,
        description, tags, examples), version, URL (for HTTP-transport agents),
        well-known status, and last-seen time.
        Use this when you need more context about an agent's skills before invoking.

        Parameters:
        - agent_name (required): The name of the agent (from list_known_agents)

        Returns: { agentName, description, version, url, isWellKnown, lastSeen,
          skills[{ id, name, description, tags, examples }] }

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
        2. If the summary is insufficient, call get_agent_details for full skill metadata
        3. Call invoke_agent with the desired agent_name, skill, and message
        4. When the agent completes, a follow-up message will arrive in the
           conversation containing a working memory key. Call
           get_from_working_memory with that key to retrieve the full result,
           then present it to the user.
        """;
}
