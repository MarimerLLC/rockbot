using System.Text.RegularExpressions;

namespace RockBot.Memory;

/// <summary>
/// Detects free-text content that reads like an agent-self capability claim
/// ("blocked", "cannot", "wrapper limitation", etc.) so the soft-gate on
/// <c>save_to_working_memory</c> and <c>save_memory</c> can tag it as an
/// observation rather than promote it directly to a capability claim.
/// </summary>
/// <remarks>
/// This is intentionally a low-precision keyword filter — false positives are tolerable
/// (the entry still writes; only the <c>kind=observation</c> tag is added) and false
/// negatives are recoverable (the dream service can promote later). The filter is not
/// the place for semantic precision; the verify shape on real claims is.
/// </remarks>
public static partial class ObservationLanguageDetector
{
    [GeneratedRegex(
        @"\b(blocked|cannot|wrapper limitation|not supported|does not expose)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ClaimLanguagePattern();

    /// <summary>
    /// Returns <c>true</c> when the given content contains language characteristic of a
    /// capability claim. Returns <c>false</c> for null, empty, or whitespace-only input.
    /// </summary>
    public static bool LooksLikeCapabilityClaim(string? content) =>
        !string.IsNullOrWhiteSpace(content) && ClaimLanguagePattern().IsMatch(content);

    /// <summary>The tag value applied to entries flagged by the soft gate.</summary>
    public const string ObservationTag = "kind=observation";

    /// <summary>Informational hint appended to the tool result for flagged entries.</summary>
    public const string ObservationHint =
        "Note: this looks like a capability claim. Agent-self capability claims are tracked as observations and require a structured verify shape (set internally by the dream service or recovery layer) to become claims that the read-side verifier can falsify.";

    /// <summary>
    /// Applies the Phase 2 soft gate to a memory write. If the content looks like a
    /// capability claim and the entry is not already tagged as an observation, returns
    /// the original tag list augmented with <see cref="ObservationTag"/> and a non-empty
    /// hint to surface to the LLM. Otherwise returns the inputs unchanged with an empty hint.
    /// Writes are never blocked.
    /// </summary>
    public static (IReadOnlyList<string>? Tags, string Hint) ApplySoftGate(
        string? content, IReadOnlyList<string>? existingTags)
    {
        if (!LooksLikeCapabilityClaim(content))
            return (existingTags, "");

        if (existingTags is not null
            && existingTags.Any(t => string.Equals(t, ObservationTag, StringComparison.OrdinalIgnoreCase)))
        {
            return (existingTags, "");
        }

        var augmented = new List<string>(existingTags ?? []) { ObservationTag };
        return (augmented, " " + ObservationHint);
    }

    [GeneratedRegex(
        @"\b(?<server>[a-z][a-z0-9-]*)/(?<tool>[a-z_][a-z_0-9]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ToolReferencePattern();

    /// <summary>
    /// Path segments that follow the <c>server/tool</c> shape syntactically but are
    /// memory namespaces, not MCP server names. Excluding them prevents the
    /// observation-eviction filter from treating working-memory keys like
    /// <c>shared/patrol/...</c> or long-term memory category paths like
    /// <c>user-preferences/family/...</c> as references to MCP tools.
    /// </summary>
    /// <remarks>
    /// Real MCP server names follow the convention <c>name-mcp</c> (e.g.
    /// <c>calendar-mcp</c>, <c>todo-mcp</c>) — none collide with this list.
    /// </remarks>
    private static readonly HashSet<string> NamespacePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Working-memory namespaces
        "shared",
        "patrol",
        "worker",
        "subagent",
        "session",
        // Long-term memory category roots
        "user-preferences",
        "agent-identity",
        "agent-knowledge",
        "project-context",
        "active-plans",
        "active-tasks",
        "subagent-whiteboards",
        "claim",
        "episodic",
        "general",
        // Skill index / catch-alls that aren't server names
        "mcp",
        "skill",
        "skills",
        // URL-shaped tokens that the regex matches but aren't tool refs
        "http",
        "https",
        "file",
        "data",
    };

    /// <summary>
    /// Returns distinct <c>(server, tool)</c> pairs named in the content using the
    /// <c>server/tool</c> shape conventionally used in observation text and
    /// recovery error messages (e.g. <c>calendar-mcp/search_emails</c>). Returns
    /// an empty list for null or empty input. Used by Amendment 1 step 4 to
    /// opportunistically verify observations against the tool-call log.
    /// Matches whose <c>server</c> component is a known memory-namespace prefix
    /// (see <see cref="NamespacePrefixes"/>) are dropped — those are path
    /// segments, not server identifiers.
    /// </summary>
    public static IReadOnlyList<(string Server, string Tool)> TryExtractToolReferences(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];

        var seen = new HashSet<(string, string)>(
            new ToolReferenceComparer());
        var refs = new List<(string Server, string Tool)>();

        foreach (Match match in ToolReferencePattern().Matches(content))
        {
            var server = match.Groups["server"].Value;
            var tool = match.Groups["tool"].Value;
            if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(tool))
                continue;
            if (NamespacePrefixes.Contains(server))
                continue;
            var pair = (server, tool);
            if (seen.Add(pair)) refs.Add(pair);
        }

        return refs;
    }

    private sealed class ToolReferenceComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(
                obj.Item1.ToLowerInvariant(),
                obj.Item2.ToLowerInvariant());
    }
}
