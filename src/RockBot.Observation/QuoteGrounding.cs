using System.Text.RegularExpressions;

namespace RockBot.Observation;

/// <summary>
/// Mechanical validation of <see cref="ProposedObservation.Quote"/> against
/// the source <see cref="TranscriptTurn"/> it claims to come from.
/// Quote-grounding is the single biggest anti-hallucination lever in the
/// pipeline (per design): an observation that cannot be backed by a
/// verbatim, in-source quote is dropped before it reaches the candidate pool.
/// </summary>
internal static class QuoteGrounding
{
    /// <summary>
    /// Minimum quote length (after whitespace normalisation). Below this the
    /// quote carries little evidentiary weight and is treated as ungrounded
    /// regardless of substring match.
    /// </summary>
    public const int MinQuoteLength = 10;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Returns the subset of <paramref name="proposals"/> whose quote is
    /// substring-present in the cited turn's content (after whitespace
    /// normalisation). Proposals that cite a non-existent turn or whose
    /// quote is too short to count as evidence are dropped.
    /// </summary>
    public static IEnumerable<ProposedObservation> Filter(
        IEnumerable<ProposedObservation> proposals,
        IReadOnlyList<TranscriptTurn> conversationTurns)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(conversationTurns);

        var byTurnId = conversationTurns.ToDictionary(
            t => t.TurnId,
            t => Normalize(t.Content),
            StringComparer.Ordinal);

        foreach (var proposal in proposals)
        {
            if (!byTurnId.TryGetValue(proposal.TurnId, out var normalisedTurn))
                continue;

            var normalisedQuote = Normalize(proposal.Quote);
            if (normalisedQuote.Length < MinQuoteLength)
                continue;

            if (normalisedTurn.Contains(normalisedQuote, StringComparison.OrdinalIgnoreCase))
                yield return proposal;
        }
    }

    /// <summary>
    /// Whitespace-normalised, trimmed lowercase form for substring comparison.
    /// </summary>
    internal static string Normalize(string text) =>
        Whitespace.Replace(text ?? string.Empty, " ").Trim();
}
