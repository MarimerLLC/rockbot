using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Provides a skill guide for the A2A caller tools.
/// </summary>
public sealed class A2ACallerSkillProvider : IToolSkillProvider
{
    public string Name => "a2a";
    public string Summary => "Register, invoke, and manage external A2A agents (register_agent, unregister_agent, invoke_agent, list_known_agents, get_agent_details).";

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
        - data (optional): Structured payload sent as a DataPart (use for nested objects/arrays)
        - metadata (optional): Per-skill control parameters that the target agent advertises
          (e.g. providerId, count, since). Look for "metadata parameters" or "metadata keys"
          in the skill description. Values must be primitives (string, number, boolean) — use
          'data' for anything nested.
        - timeout_minutes (optional): How long to wait (default 5 minutes)

        Returns: task_id for tracking

        Example: filter recent-mentions to a single platform
            invoke_agent(
              agent_name="SocialAgent",
              skill="recent-mentions",
              message="Recent mentions for Bluesky.",
              metadata={"providerId": "bluesky", "count": 10}
            )

        ## register_agent
        Register or update an HTTP-based A2A agent in the directory. The agent
        becomes available for invoke_agent immediately and is persisted across restarts.
        When updating an existing agent, only the provided fields are changed —
        omitted fields (auth, description, skills) are preserved from the existing entry.

        Parameters:
        - agent_name (required): A unique name for the agent
        - url (required): Base URL for the agent's A2A endpoint
        - description (optional): Human-readable description
        - skills (optional): Array of { id, name, description }
        - auth_header_name (optional): HTTP header name for auth (e.g. "Authorization", "X-Api-Key")
        - auth_header_value_base64 (optional): Base64-encoded header value (must pair with auth_header_name)

        ## unregister_agent
        Remove an agent from the directory. Well-known agents (statically configured)
        cannot be removed.

        Parameters:
        - agent_name (required): The name of the agent to remove

        ## Usage pattern
        1. Register external agents with register_agent (URL + optional auth + skills)
        2. Call list_known_agents to see what agents and skills are available
        3. If the summary is insufficient, call get_agent_details for full skill metadata
        4. Call invoke_agent with the desired agent_name, skill, and message
        5. When the agent completes, a follow-up message will arrive in the
           conversation containing a working memory key. Call
           get_from_working_memory with that key to retrieve the full result,
           then present it to the user.

        ## Important: use the right skill directly
        - Call the skill that matches the user's request. Do NOT call query-availability
          or other skills as a prerequisite — the target agent handles its own availability
          logic within its skill implementation.
        - Use the skill ID from list_known_agents. The target agent uses fuzzy matching,
          so close paraphrases will work, but using the listed ID is most reliable.
        - Only call one invoke_agent per user request unless the user explicitly asks
          you to contact multiple agents.
        """;
}
