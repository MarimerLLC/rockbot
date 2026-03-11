using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RockBot.Host;

namespace RockBot.A2A;

/// <summary>
/// Generates a one-sentence LLM summary of an <see cref="AgentCard"/>'s capabilities,
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
                You are summarizing an AI agent's capabilities for another AI agent that must decide which agent to delegate tasks to.
                Write a single sentence (15-25 words) that captures what the '{card.AgentName}' agent specializes in and what kinds of requests it should handle.
                Focus on what makes it distinct and when to choose it over other agents.{descLine}
                Skills:
                {skillBlock}
                Respond with only the sentence, no preamble or explanation.
                """;

            var messages = new[] { new ChatMessage(ChatRole.User, prompt) };
            var response = await llmClient.GetResponseAsync(messages, ModelTier.Low, cancellationToken: ct);
            return response.Text?.Trim() ?? fallback;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate LLM summary for agent '{AgentName}'", card.AgentName);
            return fallback;
        }
    }
}
