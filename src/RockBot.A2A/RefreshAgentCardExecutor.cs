using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// On-demand <c>refresh_agent_card</c> tool: re-fetches a peer's
/// <c>/.well-known/agent-card.json</c> so an LLM can recover from stale
/// capability data without waiting for the periodic refresh cycle.
/// </summary>
internal sealed class RefreshAgentCardExecutor(
    IAgentDirectory directory,
    AgentCardSummarizer summarizer,
    ILogger<RefreshAgentCardExecutor> logger) : IToolExecutor
{
    public async Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments) ?? [];
        }
        catch
        {
            return Error(request, "Invalid arguments JSON.");
        }

        if (!args.TryGetValue("agent_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return Error(request, "Missing required parameter: agent_name");

        var agentName = nameEl.GetString()!;

        var result = await directory.RefreshAgentCardAsync(agentName, ct);

        if (result.SkillsChanged)
        {
            var card = directory.GetAgent(result.AgentName);
            if (card is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var summary = await summarizer.SummarizeAsync(card, CancellationToken.None);
                        directory.SetSummary(result.AgentName, summary);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Failed to regenerate LLM summary for '{AgentName}' after refresh",
                            result.AgentName);
                    }
                }, CancellationToken.None);
            }
        }

        var refreshedCard = directory.GetAgent(result.AgentName);
        var skillCount = refreshedCard?.Skills?.Count ?? 0;

        var status = (result.Refreshed, result.Reason) switch
        {
            (true, _) => result.SkillsChanged ? "refreshed (skills changed)" : "refreshed (no changes)",
            (false, "agent not found") => "not found",
            (false, "offline override") => "skipped (offline override)",
            (false, _) => "skipped",
        };

        var payload = new
        {
            agentName = result.AgentName,
            status,
            refreshed = result.Refreshed,
            skillsChanged = result.SkillsChanged,
            reason = result.Reason,
            skillCount,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        return new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = json,
            IsError = false,
        };
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
