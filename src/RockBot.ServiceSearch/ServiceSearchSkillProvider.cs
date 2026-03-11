using RockBot.Tools;

namespace RockBot.ServiceSearch;

/// <summary>
/// Provides a skill guide for the search_known_services tool.
/// </summary>
public sealed class ServiceSearchSkillProvider : IToolSkillProvider
{
    public string Name => "service-search";
    public string Summary => "Search all known A2A agents and MCP servers by keyword to find the right service for a task (search_known_services).";

    public string GetDocument() =>
        """
        # Service Search Guide

        ## search_known_services
        BM25 keyword search across ALL known services — both A2A agents and MCP servers —
        in a single call. Returns a ranked list of candidates with summaries and top tool/skill
        names so you can identify the right service without calling list_known_agents and
        mcp_list_services separately.

        Parameters:
        - query (required): Keywords describing the task or capability
          Examples: "reschedule meeting", "edit python script", "lookup customer crm",
                    "aws spend audit", "send email", "search web"

        Returns: { results: [ { id, type, summary, relevance_score, top_skills | top_tools } ] }

        ## Reading the results

        Each result has:
        - id: The name to pass to the next tool (agent_name or server_name)
        - type: "a2a" or "mcp" — this determines how you interact with the service
        - summary: LLM-generated description of the service's purpose
        - relevance_score: [0, 1] — 1.0 means top match; below ~0.3 consider keeping searching
        - top_skills: (A2A only) 2–3 skill IDs available on the agent
        - top_tools: (MCP only) 2–3 tool names available on the server

        ## Deciding what to do with a result

        If type == "mcp":
          → Use mcp_invoke_tool with the id as server_name (synchronous, returns immediately)
          → For tool details first: mcp_get_service_details(server_name=id)

        If type == "a2a":
          → Use invoke_agent with the id as agent_name (asynchronous, result arrives later)
          → For skill details first: get_agent_details(agent_name=id)

        ## When to use this vs individual list tools

        Use search_known_services when:
        - You have a task and need to find which service can handle it
        - You want a single call that covers both agent and MCP namespaces
        - The context hint (see below) shows a candidate but you want to confirm ranking

        Use list_known_agents or mcp_list_services when:
        - You want to browse everything available (not task-specific)
        - search_known_services returned no results (no keyword overlap)
        - You need last-seen times or other directory metadata

        ## Context hints
        When a request arrives, the system may automatically surface 1–2 relevant services
        as a hint in the context (labeled "Potentially relevant services"). These are the
        same BM25 results you would get from calling search_known_services. If the hint
        already identifies the right service with a high relevance_score, you can skip the
        explicit tool call and proceed directly to mcp_invoke_tool or invoke_agent.

        ## Typical routing workflow
        1. Check context hint — if a service is already surfaced with high relevance, use it
        2. If unsure, call search_known_services(query="<task keywords>")
        3. Read the top result: check type, summary, and top_skills/top_tools
        4. If relevance_score < 0.3 or summary doesn't match, call mcp_list_services +
           list_known_agents to browse manually
        5. Once identified: invoke with mcp_invoke_tool (mcp) or invoke_agent (a2a)
        """;
}
