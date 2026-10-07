namespace RockBot.Tools.Mcp;

/// <summary>A typed tool and how well it matched a <see cref="McpToolSearch"/> query.</summary>
public sealed record McpToolMatch(McpWrapperTool Tool, int Score);

/// <summary>
/// Keyword scoring for <c>mcp_find_tools</c>, ported from mcp-aggregator#42's <c>find_tools</c>
/// so both gateways rank alike.
/// <list type="bullet">
///   <item>The whole query, normalized, equal to the typed name or the tool name: +1000.</item>
///   <item>
///     Per query token (lowercase alphanumeric run, longer than one character), the first that
///     applies of: equals a token of the tool name +80, inside the tool name +50, inside the
///     typed name +30.
///   </item>
///   <item>Then, per token, +10 if the description has it and +5 if the server's name, display
///   name or summary does.</item>
/// </list>
/// </summary>
public static class McpToolSearch
{
    public const int ExactMatch = 1000;
    public const int ToolNameToken = 80;
    public const int InToolName = 50;
    public const int InTypedName = 30;
    public const int InDescription = 10;
    public const int InServer = 5;

    /// <summary>
    /// The tools that score above zero for <paramref name="query"/>, best first (ties by typed
    /// name), at most <paramref name="limit"/>.
    /// </summary>
    public static IReadOnlyList<McpToolMatch> Rank(
        string query,
        IEnumerable<McpWrapperTool> tools,
        IReadOnlyList<McpServerSummary> servers,
        int limit)
    {
        var normalized = query.Trim().ToLowerInvariant();
        var tokens = Tokens(normalized).Distinct().ToList();
        if (normalized.Length == 0 || limit <= 0)
            return [];

        var serverText = servers.ToDictionary(
            s => s.ServerName,
            s => $"{s.ServerName} {s.DisplayName} {s.Summary}".ToLowerInvariant(),
            StringComparer.OrdinalIgnoreCase);

        return tools
            .Select(t => new McpToolMatch(t, Score(t, normalized, tokens,
                serverText.GetValueOrDefault(t.ServerName) ?? t.ServerName.ToLowerInvariant())))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Tool.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    internal static int Score(McpWrapperTool tool, string normalizedQuery, IReadOnlyList<string> tokens, string serverText)
    {
        var typedName = tool.Name.ToLowerInvariant();
        var toolName = tool.ToolName.ToLowerInvariant();
        var description = tool.Description?.ToLowerInvariant() ?? string.Empty;
        var nameTokens = Tokens(SplitCamelCase(tool.ToolName).ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        var score = normalizedQuery == typedName || normalizedQuery == toolName ? ExactMatch : 0;

        foreach (var token in tokens)
        {
            if (nameTokens.Contains(token))
                score += ToolNameToken;
            else if (toolName.Contains(token, StringComparison.Ordinal))
                score += InToolName;
            else if (typedName.Contains(token, StringComparison.Ordinal))
                score += InTypedName;

            if (description.Contains(token, StringComparison.Ordinal))
                score += InDescription;
            if (serverText.Contains(token, StringComparison.Ordinal))
                score += InServer;
        }

        return score;
    }

    /// <summary>Lowercase alphanumeric runs longer than one character.</summary>
    internal static IEnumerable<string> Tokens(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inToken = i < text.Length && char.IsAsciiLetterOrDigit(text[i]);
            if (inToken && start < 0)
            {
                start = i;
            }
            else if (!inToken && start >= 0)
            {
                if (i - start > 1)
                    yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    // sendEmail → send Email, so a camelCase tool name has the same tokens as send_email.
    private static string SplitCamelCase(string name)
    {
        var chars = new List<char>(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
                chars.Add(' ');
            chars.Add(name[i]);
        }
        return new string([.. chars]);
    }
}
