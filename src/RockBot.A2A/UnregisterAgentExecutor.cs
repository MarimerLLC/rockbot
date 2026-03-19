using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Removes an agent from the directory. Well-known agents cannot be removed.
/// </summary>
internal sealed class UnregisterAgentExecutor(
    IAgentDirectory directory,
    ILogger<UnregisterAgentExecutor> logger) : IToolExecutor
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
            return Task.FromResult(Error(request, "Invalid arguments JSON."));
        }

        if (!args.TryGetValue("agent_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return Task.FromResult(Error(request, "Missing required parameter: agent_name"));

        var agentName = nameEl.GetString()!;

        var existing = directory.GetAgent(agentName);
        if (existing is null)
        {
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"No agent named '{agentName}' found in the directory.",
                IsError = false
            });
        }

        directory.Remove(agentName);
        logger.LogInformation("Unregistered agent '{AgentName}'", agentName);

        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = $"Agent '{agentName}' removed from the directory.",
            IsError = false
        });
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
