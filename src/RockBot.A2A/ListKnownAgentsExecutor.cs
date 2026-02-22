using System.Text.Json;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Returns agents known to the local directory, optionally filtered by skill.
/// </summary>
internal sealed class ListKnownAgentsExecutor(IAgentDirectory directory) : IToolExecutor
{
    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
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
            args = [];
        }

        string? skillFilter = args.TryGetValue("skill", out var skillEl) && skillEl.ValueKind == JsonValueKind.String
            ? skillEl.GetString()
            : null;

        var agents = string.IsNullOrWhiteSpace(skillFilter)
            ? directory.GetAllAgents()
            : directory.FindBySkill(skillFilter);

        if (agents.Count == 0)
        {
            var noAgentsMsg = string.IsNullOrWhiteSpace(skillFilter)
                ? "No agents have announced themselves yet."
                : $"No agents found with skill '{skillFilter}'.";

            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = noAgentsMsg,
                IsError = false
            });
        }

        var items = agents.Select(a => new
        {
            agentName = a.AgentName,
            description = a.Description,
            skills = a.Skills?.Select(s => new { id = s.Id, description = s.Description }).ToArray()
        });

        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = json,
            IsError = false
        });
    }
}
