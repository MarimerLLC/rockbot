using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Tools.Mcp.Recovery.Providers;

/// <summary>
/// Resolves <c>accountId</c> for the <c>calendar-mcp</c> server by calling
/// <c>list_accounts</c> and returning the resulting account IDs as a fan-out
/// collection. The recovery executor issues one tool call per account and
/// aggregates the responses.
/// </summary>
public sealed class AccountIdFanoutProvider(
    McpInvokeDelegate invoke,
    ILogger<AccountIdFanoutProvider> logger) : IToolArgumentDefaultsProvider
{
    private const string TargetServer = "calendar-mcp";
    private const string TargetField = "accountId";
    private const string ListAccountsTool = "list_accounts";

    public bool CanResolve(string serverName, string toolName, string fieldName) =>
        string.Equals(serverName, TargetServer, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(fieldName, TargetField, StringComparison.OrdinalIgnoreCase);

    public async Task<ResolvedDefault?> ResolveAsync(ResolveContext ctx, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            [McpHeaders.ServerName] = TargetServer
        };

        var request = new ToolInvokeRequest
        {
            ToolCallId = $"recovery-list-accounts-{Guid.NewGuid():N}",
            ToolName = ListAccountsTool,
            Arguments = "{}"
        };

        var response = await invoke(request, headers, ct);
        if (response.IsError)
        {
            logger.LogWarning("AccountIdFanoutProvider: list_accounts failed: {Content}", response.Content);
            return null;
        }

        var ids = ExtractAccountIds(response.Content);
        if (ids.Count == 0)
        {
            logger.LogWarning("AccountIdFanoutProvider: list_accounts returned no account IDs");
            return null;
        }

        return new ResolvedDefault(ids, RequiresFanOut: true);
    }

    /// <summary>
    /// Extracts account IDs from a list_accounts response. Tolerant of common shapes:
    /// a JSON array of strings, an array of objects with id/accountId/email properties,
    /// or an object containing such an array under common keys.
    /// </summary>
    internal static List<string> ExtractAccountIds(string? content)
    {
        var ids = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return ids;

        try
        {
            using var doc = JsonDocument.Parse(content);
            CollectIds(doc.RootElement, ids);
        }
        catch (JsonException)
        {
            // Not JSON; nothing to extract.
        }

        return ids
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly string[] IdKeys = ["id", "accountId", "account_id", "email", "name"];
    private static readonly string[] CollectionKeys = ["accounts", "items", "results", "data"];

    private static void CollectIds(JsonElement element, List<string> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        ids.Add(item.GetString() ?? string.Empty);
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var key in IdKeys)
                        {
                            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                            {
                                ids.Add(v.GetString() ?? string.Empty);
                                break;
                            }
                        }
                    }
                }
                break;

            case JsonValueKind.Object:
                foreach (var key in CollectionKeys)
                {
                    if (element.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        CollectIds(arr, ids);
                        return;
                    }
                }
                break;
        }
    }
}
