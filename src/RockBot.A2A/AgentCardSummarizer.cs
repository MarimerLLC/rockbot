using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.A2A;

/// <summary>
/// Generates a 2-4 sentence LLM summary of an <see cref="AgentCard"/>'s capabilities,
/// following the same pattern used for MCP server summaries.
/// Falls back to the card's description or a skill-list sentence if the LLM is unavailable.
/// </summary>
internal sealed class AgentCardSummarizer(
    IServiceProvider services,
    ILogger<AgentCardSummarizer> logger)
{
    public async Task<string> SummarizeAsync(AgentCard card, CancellationToken ct)
    {
        var skillIds = card.Skills?.Select(s => s.Id).ToList() ?? [];
        var fallback = !string.IsNullOrWhiteSpace(card.Description)
            ? card.Description
            : skillIds.Count > 0
                ? $"Agent with skill(s): {string.Join(", ", skillIds)}"
                : "Agent with no advertised skills.";

        var llmClient = services.GetService<ILlmClient>();
        if (llmClient is null) return fallback;

        try
        {
            var skillLines = (card.Skills ?? [])
                .Take(20)
                .Select(s => $"- {s.Id}: {s.Name}" + (s.Description != null ? $" — {s.Description}" : ""));
            var skillBlock = skillLines.Any()
                ? string.Join("\n", skillLines)
                : "  (none)";

            var descLine = !string.IsNullOrWhiteSpace(card.Description)
                ? $"\nAgent description: {card.Description}"
                : string.Empty;

            var prompt = $"""
                You are summarizing an AI agent's capabilities for another AI agent that must decide
                which agent to delegate tasks to. The agent sees ONLY this summary when deciding —
                it does not see skill details until it calls get_agent_details.

                Write 2-4 sentences (40-80 words) for the '{card.AgentName}' agent that:
                1. State what domain or problem space it covers
                2. List the CATEGORIES of tasks it can handle (e.g. "research and summarize topics,
                   draft documents, analyze data")
                3. Mention any notable specifics (e.g. specialized knowledge, integrations, platforms)

                The summary must give enough detail that another agent can confidently decide "this is
                the agent I need for X" without seeing individual skill details.{descLine}
                Skills:
                {skillBlock}
                Respond with only the summary, no preamble or explanation.
                """;

            var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
            var response = await llmClient.GetResponseAsync(messages, ModelTier.Low, options: null, cancellationToken: ct);
            return response.Text?.Trim() ?? fallback;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate LLM summary for agent '{AgentName}'", card.AgentName);
            return fallback;
        }
    }
}
