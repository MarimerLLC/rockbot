using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServer.Introspection.Tools;

[McpServerToolType]
public sealed class CopilotUsageTools(IConfiguration configuration)
{
    private string MetricsFilePath =>
        configuration["CopilotUsage:Path"] ?? "/data/agent/copilot-usage.json";

    [McpServerTool(Name = "get_copilot_usage")]
    [Description(
        "Returns GitHub Copilot usage metrics including premium request count, " +
        "token consumption (input/output), cumulative cost multiplier, session count, " +
        "and rate-limit retries. These metrics track actual billing-relevant usage " +
        "since the agent last started (or since metrics were last reset).")]
    public async Task<string> GetCopilotUsageAsync()
    {
        if (!File.Exists(MetricsFilePath))
            return "No Copilot usage data available. The agent may not be using the Copilot provider, " +
                   "or no requests have been made yet.";

        try
        {
            var json = await File.ReadAllTextAsync(MetricsFilePath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var premiumRequests = GetLong(root, "premiumRequests");
            var sessions = GetLong(root, "sessionsCreated");
            var inputTokens = GetLong(root, "totalInputTokens");
            var outputTokens = GetLong(root, "totalOutputTokens");
            var costMultiplier = GetDouble(root, "totalCostMultiplier");
            var rateLimitRetries = GetLong(root, "rateLimitRetries");
            var lastModel = GetString(root, "lastModel");
            var lastUpdated = GetString(root, "lastUpdated");

            return $"""
                Copilot Usage Summary:
                - Premium requests (billing events): {premiumRequests}
                - Sessions created: {sessions}
                - Input tokens consumed: {inputTokens:N0}
                - Output tokens produced: {outputTokens:N0}
                - Total tokens: {inputTokens + outputTokens:N0}
                - Cumulative cost multiplier: {costMultiplier:F2}
                - Rate-limit retries: {rateLimitRetries}
                - Last model used: {lastModel ?? "unknown"}
                - Last updated: {lastUpdated ?? "unknown"}
                """;
        }
        catch (Exception ex)
        {
            return $"Error reading Copilot usage metrics: {ex.Message}";
        }
    }

    [McpServerTool(Name = "reset_copilot_usage")]
    [Description(
        "Resets all Copilot usage metric counters to zero. Use this to start " +
        "fresh tracking, for example at the beginning of a billing period.")]
    public async Task<string> ResetCopilotUsageAsync()
    {
        try
        {
            var dir = Path.GetDirectoryName(MetricsFilePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(MetricsFilePath, "{}");
            return "Copilot usage metrics have been reset to zero.";
        }
        catch (Exception ex)
        {
            return $"Error resetting Copilot usage metrics: {ex.Message}";
        }
    }

    private static long GetLong(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) && el.TryGetInt64(out var v) ? v : 0;

    private static double GetDouble(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) && el.TryGetDouble(out var v) ? v : 0;

    private static string? GetString(JsonElement root, string prop) =>
        root.TryGetProperty(prop, out var el) ? el.GetString() : null;
}
