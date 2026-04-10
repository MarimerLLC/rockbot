using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Classifies tool-call failures into categories for the dream system's cross-session
/// analysis. The classifier uses pattern matching on error messages to distinguish
/// structural mistakes (wrong tool/param names) from transient external failures
/// (timeouts, rate limits) and data-level confusion (wrong path namespace).
/// </summary>
internal static partial class ToolCallFailureClassifier
{
    // ── External failure patterns ────────────────────────────────────────────

    [GeneratedRegex(
        @"\b(?:timeout|timed?\s*out|deadline\s+exceeded)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TimeoutPattern();

    [GeneratedRegex(
        @"\b(?:rate\s*limit|throttl(?:ed|ing)?|too\s+many\s+requests|429)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RateLimitPattern();

    [GeneratedRegex(
        @"\b(?:service\s+unavailable|503|502|connection\s+refused|network\s+error|ECONNREFUSED|ECONNRESET)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ServiceUnavailablePattern();

    [GeneratedRegex(
        @"\b(?:auth(?:entication|orization)?\s+(?:failed|expired|denied|error)|401|403|unauthorized|forbidden)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AuthPattern();

    // ── Structural failure patterns ──────────────────────────────────────────

    [GeneratedRegex(
        @"\b(?:unknown\s+tool|tool\s+not\s+found|unrecognized\s+(?:tool|function))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UnknownToolPattern();

    [GeneratedRegex(
        @"\b(?:missing\s+required\s+param|invalid\s+arguments?|validation\s+error|pydantic|required\s+field|unexpected\s+keyword|unknown\s+param)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex InvalidParamsPattern();

    [GeneratedRegex(
        @"\b(?:invalid\s+(?:json|format|syntax)|(?:json|parse)\s+error|malformed)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MalformedInputPattern();

    // ── Data failure patterns ────────────────────────────────────────────────

    [GeneratedRegex(
        @"\b(?:resource\s+not\s+found|(?:file|folder|path|item)\s+not\s+found|404|does\s+not\s+exist|no\s+such\s+(?:file|directory))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ResourceNotFoundPattern();

    /// <summary>
    /// Classifies a tool-call failure based on available context signals.
    /// </summary>
    /// <param name="errorMessage">The error message returned by the tool call.</param>
    /// <param name="isUnknownTool">
    /// <c>true</c> when the tool name did not resolve to any registered tool.
    /// </param>
    /// <param name="isThrashing">
    /// <c>true</c> when the <see cref="AgentLoopRunner.RepetitiveToolCallDetector"/>
    /// has flagged this call as part of a repetitive loop.
    /// </param>
    /// <returns>The classified failure category.</returns>
    public static ToolCallFailureCategory Classify(
        string? errorMessage,
        bool isUnknownTool = false,
        bool isThrashing = false)
    {
        if (isThrashing)
            return ToolCallFailureCategory.Thrashing;

        if (isUnknownTool)
            return ToolCallFailureCategory.Structural;

        if (string.IsNullOrWhiteSpace(errorMessage))
            return ToolCallFailureCategory.Structural; // Unknown error, default to structural

        // Check external patterns first — they indicate transient issues that
        // should not be penalised.
        if (TimeoutPattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.External;

        if (RateLimitPattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.External;

        if (ServiceUnavailablePattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.External;

        if (AuthPattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.External;

        // Structural failures — wrong tool/param names, invalid format.
        if (UnknownToolPattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.Structural;

        if (InvalidParamsPattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.Structural;

        if (MalformedInputPattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.Structural;

        // Data failures — path/resource confusion.
        if (ResourceNotFoundPattern().IsMatch(errorMessage))
            return ToolCallFailureCategory.Data;

        // Default to structural for unrecognised error patterns.
        return ToolCallFailureCategory.Structural;
    }

    /// <summary>
    /// Finds the closest matching tool name from the available tools when the LLM
    /// generates a tool name that doesn't exist (e.g. <c>search_files{}</c> instead of
    /// <c>search_files</c>). Returns null if no close match is found.
    /// </summary>
    /// <param name="requestedName">The tool name the LLM generated.</param>
    /// <param name="availableNames">The set of registered tool names.</param>
    /// <param name="maxDistance">Maximum edit distance to consider a match (default 3).</param>
    /// <returns>The closest tool name, or null if none is within the distance threshold.</returns>
    public static string? FindClosestToolName(
        string requestedName,
        IEnumerable<string> availableNames,
        int maxDistance = 3)
    {
        if (string.IsNullOrEmpty(requestedName))
            return null;

        // First try stripping trailing non-alphanumeric chars (handles "search_files{}" → "search_files").
        var stripped = requestedName.TrimEnd('{', '}', '(', ')', '[', ']', ' ');
        string? bestMatch = null;
        var bestDistance = int.MaxValue;

        foreach (var name in availableNames)
        {
            // Exact match after stripping
            if (stripped.Equals(name, StringComparison.OrdinalIgnoreCase))
                return name;

            var distance = LevenshteinDistance(
                requestedName.ToLowerInvariant(),
                name.ToLowerInvariant());

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestMatch = name;
            }
        }

        return bestDistance <= maxDistance ? bestMatch : null;
    }

    /// <summary>
    /// Computes the Levenshtein (edit) distance between two strings.
    /// </summary>
    internal static int LevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source))
            return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target))
            return source.Length;

        var sourceLength = source.Length;
        var targetLength = target.Length;

        // Use a single-row DP approach for O(min(m,n)) space.
        var previousRow = new int[targetLength + 1];
        var currentRow = new int[targetLength + 1];

        for (var j = 0; j <= targetLength; j++)
            previousRow[j] = j;

        for (var i = 1; i <= sourceLength; i++)
        {
            currentRow[0] = i;
            for (var j = 1; j <= targetLength; j++)
            {
                var cost = source[i - 1] == target[j - 1] ? 0 : 1;
                currentRow[j] = Math.Min(
                    Math.Min(currentRow[j - 1] + 1, previousRow[j] + 1),
                    previousRow[j - 1] + cost);
            }

            (previousRow, currentRow) = (currentRow, previousRow);
        }

        return previousRow[targetLength];
    }
}
