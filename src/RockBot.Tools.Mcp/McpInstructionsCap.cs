namespace RockBot.Tools.Mcp;

/// <summary>
/// Size policy for a downstream server's own <c>instructions</c> (#614). Servers built the way
/// mcp-aggregator#48 recommends keep them short and put their full guide behind a tool; older
/// ones may send pages. Past the cap the text is cut on a line boundary and ends with an explicit
/// marker — never a silent truncation — that says how much was dropped and names the server's
/// guide-like tools, if it has any, as the way to the rest.
/// </summary>
public static class McpInstructionsCap
{
    /// <summary>The cap on instructions inlined in <c>mcp_get_service_details</c>.</summary>
    public const int DetailsMaxChars = 2000;

    /// <summary>The cap on instructions in the prompt that writes a server's summary.</summary>
    public const int SummaryPromptMaxChars = 8000;

    private static readonly string[] GuideWords = ["guide", "skill", "help", "instructions", "readme", "docs"];

    /// <summary>The tools in <paramref name="toolNames"/> whose names say they explain the server.</summary>
    public static IReadOnlyList<string> GuideTools(IEnumerable<string> toolNames) =>
        toolNames
            .Where(n => GuideWords.Any(w => n.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>
    /// <paramref name="instructions"/> unchanged when within <paramref name="maxChars"/>;
    /// otherwise its first lines up to the cap, then the truncation marker, pointing to
    /// <paramref name="guideTools"/> (already formatted for the reader) when there are any.
    /// </summary>
    public static string? Apply(string? instructions, int maxChars, IReadOnlyList<string> guideTools)
    {
        if (string.IsNullOrEmpty(instructions) || instructions.Length <= maxChars)
            return instructions;

        // Cut at the last line break within the cap, unless that would throw away most of it.
        var cut = instructions.LastIndexOf('\n', maxChars - 1);
        if (cut < maxChars / 2)
            cut = maxChars;

        var kept = instructions[..cut].TrimEnd();
        var marker = $"[Server instructions truncated: {kept.Length} of {instructions.Length} characters shown.";
        marker += guideTools.Count > 0
            ? $" The server's own guide may cover the rest: {string.Join(", ", guideTools)}.]"
            : "]";
        return $"{kept}\n\n{marker}";
    }
}
