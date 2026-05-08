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
}
