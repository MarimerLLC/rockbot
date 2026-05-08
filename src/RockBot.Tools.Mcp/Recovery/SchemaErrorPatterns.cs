using System.Text.RegularExpressions;

namespace RockBot.Tools.Mcp.Recovery;

/// <summary>
/// Pattern matchers for common JSON-schema "missing required field" error shapes
/// emitted by MCP servers. The captured group is always the field name.
/// See <c>design/self-repair.md</c> Phase 1, Stage A.
/// </summary>
internal static class SchemaErrorPatterns
{
    private const RegexOptions Opts = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] Patterns =
    [
        // Required parameter 'X' / Required parameter "X" / Required parameter X
        new(@"Required\s+parameter\s+['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?", Opts),
        // X is required / 'X' is required / "X" is required
        new(@"['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?\s+is\s+required\b", Opts),
        // missing required argument X / missing required argument 'X'
        new(@"missing\s+required\s+argument\s+['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?", Opts),
        // expected field X / expected field 'X'
        new(@"expected\s+field\s+['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?", Opts),
        // X: must be provided
        new(@"['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?\s*:\s*must\s+be\s+provided", Opts),
    ];

    /// <summary>
    /// Attempts to extract a missing field name from an error string.
    /// Returns <c>true</c> and sets <paramref name="fieldName"/> on a match.
    /// </summary>
    public static bool TryExtractMissingField(string? errorText, out string fieldName)
    {
        fieldName = string.Empty;
        if (string.IsNullOrWhiteSpace(errorText)) return false;

        foreach (var rx in Patterns)
        {
            var m = rx.Match(errorText);
            if (m.Success)
            {
                fieldName = m.Groups["f"].Value;
                return !string.IsNullOrEmpty(fieldName);
            }
        }
        return false;
    }
}
