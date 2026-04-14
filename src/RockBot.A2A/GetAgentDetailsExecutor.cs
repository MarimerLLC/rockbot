using System.Text.Json;
using RockBot.Tools;

namespace RockBot.A2A;

/// <summary>
/// Returns the full AgentCard (including all skill fields, URL, version, and last-seen) for a named agent.
/// </summary>
internal sealed class GetAgentDetailsExecutor(IAgentDirectory directory) : IToolExecutor
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

        if (!args.TryGetValue("agent_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = "Missing required parameter: agent_name.",
                IsError = true
            });
        }

        var agentName = nameEl.GetString()!;
        var entries = directory.GetAllEntries();
        var entry = entries.FirstOrDefault(e => string.Equals(e.Card.AgentName, agentName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return Task.FromResult(new ToolInvokeResponse
            {
                ToolCallId = request.ToolCallId,
                ToolName = request.ToolName,
                Content = $"No agent named '{agentName}' found in the directory.",
                IsError = false
            });
        }

        var now = DateTimeOffset.UtcNow;
        var lastSeen = entry.IsWellKnown && entry.LastSeenAt == DateTimeOffset.MinValue
            ? "well-known (not yet seen this session)"
            : entry.IsWellKnown
                ? $"{FormatAge(now - entry.LastSeenAt)} (well-known)"
                : FormatAge(now - entry.LastSeenAt);

        var result = new
        {
            agentName = entry.Card.AgentName,
            description = entry.Card.Description,
            version = entry.Card.Version,
            url = entry.Card.Url,
            hasAuth = !string.IsNullOrEmpty(entry.Card.AuthHeaderName),
            supportsStreaming = entry.Card.SupportsStreaming,
            isWellKnown = entry.IsWellKnown,
            lastSeen,
            skills = entry.Card.Skills?.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                description = s.Description,
                tags = s.Tags,
                examples = s.Examples
            }).ToArray()
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        return Task.FromResult(new ToolInvokeResponse
        {
            ToolCallId = request.ToolCallId,
            ToolName = request.ToolName,
            Content = json,
            IsError = false
        });
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return "just now";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
