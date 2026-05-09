using System.Text.RegularExpressions;

namespace RockBot.Host;

/// <summary>
/// Deterministically maps an MCP error string to an error class for
/// <see cref="ClusterKey.ErrorClass"/>. Mirrors the patterns Phase 1 uses to
/// extract a missing required field name; falls back to <c>"unknown"</c> when
/// no pattern matches.
/// See <c>design/self-repair.md</c> Phase 5.
/// </summary>
internal static class FailureErrorClassifier
{
    public const string Unknown = "unknown";

    private const RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] Patterns =
    [
        new(@"Required\s+parameter\s+['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?", Opts),
        new(@"['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?\s+is\s+required\b", Opts),
        new(@"missing\s+required\s+argument\s+['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?", Opts),
        new(@"expected\s+field\s+['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?", Opts),
        new(@"['""]?(?<f>[A-Za-z_][A-Za-z0-9_]*)['""]?\s*:\s*must\s+be\s+provided", Opts),
    ];

    public static string Classify(string? errorText)
    {
        if (string.IsNullOrWhiteSpace(errorText)) return Unknown;

        foreach (var rx in Patterns)
        {
            var m = rx.Match(errorText);
            if (m.Success)
            {
                var name = m.Groups["f"].Value;
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }

        return Unknown;
    }
}
