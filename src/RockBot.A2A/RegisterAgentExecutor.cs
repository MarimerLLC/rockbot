using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Registers or updates an HTTP-based A2A agent in the directory.
/// Supports optional auth header configuration for agents that require API keys.
/// </summary>
internal sealed class RegisterAgentExecutor(
    IAgentDirectory directory,
    ILogger<RegisterAgentExecutor> logger) : IToolExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public Task<ToolInvokeResponse> ExecuteAsync(ToolInvokeRequest request, CancellationToken ct)
    {
        Dictionary<string, JsonElement> args;
        try
        {
            args = string.IsNullOrWhiteSpace(request.Arguments)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.Arguments, JsonOptions) ?? [];
        }
        catch
        {
            return Task.FromResult(Error(request, "Invalid arguments JSON."));
        }

        if (!TryGetString(args, "agent_name", out var agentName))
            return Task.FromResult(Error(request, "Missing required parameter: agent_name"));
        if (!TryGetString(args, "url", out var url))
            return Task.FromResult(Error(request, "Missing required parameter: url"));

        TryGetString(args, "description", out var description);
        TryGetString(args, "auth_header_name", out var authHeaderName);
        TryGetString(args, "auth_header_value_base64", out var authHeaderValueBase64);

        // Parse optional skills array
        List<AgentSkill>? skills = null;
        if (args.TryGetValue("skills", out var skillsEl) && skillsEl.ValueKind == JsonValueKind.Array)
        {
            try
            {
                skills = JsonSerializer.Deserialize<List<AgentSkill>>(skillsEl.GetRawText(), JsonOptions);
            }
            catch
            {
                return Task.FromResult(Error(request, "Invalid skills array format. Expected: [{\"id\": \"...\", \"name\": \"...\", \"description\": \"...\"}]"));
            }
        }

        // Validate auth: both header name and value must be provided together
        if (!string.IsNullOrEmpty(authHeaderName) != !string.IsNullOrEmpty(authHeaderValueBase64))
            return Task.FromResult(Error(request, "auth_header_name and auth_header_value_base64 must be provided together."));

        // Validate base64 encoding
        if (!string.IsNullOrEmpty(authHeaderValueBase64))
        {
            try
            {
                Convert.FromBase64String(authHeaderValueBase64);
            }
            catch (FormatException)
            {
                return Task.FromResult(Error(request, "auth_header_value_base64 is not valid base64."));
            }
        }

        // When updating an existing agent, preserve fields that weren't provided in this call
        // (e.g. auth config, description, skills) so a simple URL update doesn't wipe them.
        var existing = directory.GetAgent(agentName);

        var card = new AgentCard
        {
            AgentName = agentName,
            Url = url,
            Description = string.IsNullOrEmpty(description) ? existing?.Description : description,
            Skills = skills ?? existing?.Skills,
            AuthHeaderName = string.IsNullOrEmpty(authHeaderName) ? existing?.AuthHeaderName : authHeaderName,
            AuthHeaderValueBase64 = string.IsNullOrEmpty(authHeaderValueBase64) ? existing?.AuthHeaderValueBase64 : authHeaderValueBase64
        };

        directory.AddOrUpdate(card);

        var authNote = !string.IsNullOrEmpty(authHeaderName) ? $" Auth header '{authHeaderName}' configured." : "";
        var skillNote = skills is { Count: > 0 } ? $" {skills.Count} skill(s) registered." : "";
        logger.LogInformation("Registered agent '{AgentName}' at {Url}{Auth}", agentName, url, authNote);

        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = $"Agent '{agentName}' registered at {url}.{skillNote}{authNote}",
            IsError = false
        });
    }

    private static bool TryGetString(Dictionary<string, JsonElement> args, string key, out string value)
    {
        if (args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }
        value = string.Empty;
        return false;
    }

    private static ToolInvokeResponse Error(ToolInvokeRequest req, string msg) =>
        new() { ToolCallId = req.ToolCallId, ToolName = req.ToolName, Content = msg, IsError = true };
}
