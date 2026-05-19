using System.Text;
using System.Text.Json;
using RockBot.Host;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Builds an enriched error response for the LLM when a required tool field is
/// missing and no environmental default can fill it. The enricher surfaces:
/// (a) the JSON schema for the missing field, (b) any sentence of the tool
/// description that mentions the field, and (c) recent same-session calls whose
/// results plausibly produced the field. The LLM threads the value on retry
/// and is expected to save or update a skill.
///
/// See <c>design/self-repair.md</c> Amendment 1 ("Surface, don't substitute").
/// </summary>
public sealed class SchemaErrorEnricher(
    ToolSchemaCache schemas,
    IToolCallLog toolCallLog,
    ISkillStore? skillStore = null)
{
    /// <summary>Maximum number of recent calls listed in the enriched output.</summary>
    internal const int MaxRecentCallsListed = 5;

    /// <summary>How far back to scan the session log for relevant calls.</summary>
    internal static readonly TimeSpan RecentCallLookback = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Returns an enriched error string suitable for use as
    /// <see cref="ToolInvokeResponse.Content"/>. Always begins with the original
    /// error so the LLM still sees the raw signal from the MCP server.
    /// </summary>
    public async Task<string> EnrichAsync(
        string serverName,
        string toolName,
        string fieldName,
        string? sessionId,
        string originalError,
        CancellationToken ct)
    {
        McpToolDefinition? schema = null;
        try
        {
            schema = await schemas.GetAsync(serverName, toolName, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Schema fetch is best-effort; enrichment still works without it.
        }

        var sb = new StringBuilder();
        sb.AppendLine(originalError.TrimEnd());
        sb.Append("[mcp-recovery] Call to '").Append(serverName).Append('/').Append(toolName)
          .Append("' is missing required field '").Append(fieldName).AppendLine("'.");

        if (schema is not null)
        {
            var fieldSchema = ExtractFieldSchema(schema.ParametersSchema, fieldName);
            if (fieldSchema is not null)
                sb.Append("Field schema: ").AppendLine(fieldSchema);

            var hint = ExtractFieldHint(schema.Description, fieldName);
            if (hint is not null)
                sb.Append("Tool description hint: ").AppendLine(hint);
        }

        if (!string.IsNullOrEmpty(sessionId))
        {
            var recent = await TryGetRecentRelatedCallsAsync(sessionId, fieldName, ct);
            if (recent.Count > 0)
            {
                sb.AppendLine("Recent successful calls in this session that likely produced this field:");
                foreach (var c in recent)
                {
                    sb.Append("  - ").Append(c.ToolName);
                    if (!string.IsNullOrEmpty(c.ArgumentsSummary))
                        sb.Append(" (").Append(c.ArgumentsSummary).Append(')');
                    sb.Append(" @ ").AppendLine(c.Timestamp.ToString("u"));
                }
                sb.Append("Use the value of '").Append(fieldName)
                  .AppendLine("' from those results in your conversation history.");
            }
        }

        // Recovery-time skill injection: the LLM just hit a parameter-required
        // error and is about to retry. If a `mcp/{server}` skill exists, append
        // its content so the next attempt can use verified parameter shape
        // instead of re-guessing from training priors.
        try
        {
            var skillBlock = await McpServerSkillFormatter.FormatAsync(skillStore, serverName, ct);
            if (!string.IsNullOrEmpty(skillBlock))
                sb.AppendLine().Append(skillBlock);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Best-effort — never let skill injection break enrichment.
        }

        return sb.ToString().TrimEnd();
    }

    private async Task<IReadOnlyList<ToolCallEvent>> TryGetRecentRelatedCallsAsync(
        string sessionId, string fieldName, CancellationToken ct)
    {
        IReadOnlyList<ToolCallEvent> calls;
        try
        {
            calls = await toolCallLog.GetBySessionAsync(sessionId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }

        var fieldRoot = FieldRoot(fieldName);
        var cutoff = DateTimeOffset.UtcNow - RecentCallLookback;

        return calls
            .Where(c => c.Succeeded)
            .Where(c => c.Timestamp >= cutoff)
            .Where(c => CouldProduce(c, fieldRoot))
            .OrderByDescending(c => c.Timestamp)
            .Take(MaxRecentCallsListed)
            .ToList();
    }

    /// <summary>
    /// Returns the JSON schema entry for the given field as raw JSON text, or
    /// null if the parameters schema is missing or doesn't declare the field.
    /// </summary>
    internal static string? ExtractFieldSchema(string? parametersSchema, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(parametersSchema)) return null;
        try
        {
            using var doc = JsonDocument.Parse(parametersSchema);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return null;
            if (props.ValueKind != JsonValueKind.Object) return null;
            if (!props.TryGetProperty(fieldName, out var field)) return null;
            return field.GetRawText();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the first sentence of the tool description that mentions the field,
    /// or null if no such sentence exists. The output is intended for the LLM
    /// rather than for parsing, so punctuation is normalised lightly.
    /// </summary>
    internal static string? ExtractFieldHint(string? description, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;

        var sentences = description.Split(['.', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in sentences)
        {
            var trimmed = s.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Contains(fieldName, StringComparison.OrdinalIgnoreCase))
                return trimmed.TrimEnd('.') + ".";
        }
        return null;
    }

    /// <summary>
    /// Strips an "Id"/"_id" suffix from a field name for matching: <c>emailId</c>
    /// becomes <c>email</c>, <c>account_id</c> becomes <c>account</c>. Falls
    /// through unchanged for fields without the suffix.
    /// </summary>
    internal static string FieldRoot(string fieldName)
    {
        // Order matters: "_id" must be checked before "Id" because "account_id"
        // also ends with "id" (case-insensitive) and the "Id" branch would
        // strip only two characters, leaving the trailing underscore.
        if (fieldName.EndsWith("_id", StringComparison.OrdinalIgnoreCase) && fieldName.Length > 3)
            return fieldName[..^3];
        if (fieldName.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && fieldName.Length > 2)
            return fieldName[..^2];
        return fieldName;
    }

    /// <summary>
    /// Heuristic: a prior tool call could have produced the missing field if
    /// the field's root (lowercased, "Id" stripped) appears anywhere in the
    /// call's ToolName or ArgumentsSummary. For MCP calls the outer
    /// <c>ToolName</c> is always <c>mcp_invoke_tool</c> so the inner tool
    /// surfaces via <c>tool_name=X</c> in the args summary.
    /// </summary>
    internal static bool CouldProduce(ToolCallEvent call, string fieldRoot)
    {
        if (fieldRoot.Length < 2) return false;
        var needle = fieldRoot.ToLowerInvariant();
        if (call.ToolName.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        if (call.ArgumentsSummary is { Length: > 0 } args
            && args.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
