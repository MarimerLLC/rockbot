using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Default <see cref="IMemoryContradictionDetector"/>. Hot-path keyword detector for the
/// two narrow shapes Phase 3 covers: capability-claim valence inversion and feedback-rule
/// directive inversion. Outside <c>claim/capability/*</c> and <c>feedback/*</c> the
/// detector short-circuits to <see cref="ContradictionResolution.None"/> so general
/// memory writes pay zero detection cost.
/// </summary>
internal sealed partial class MemoryContradictionDetector : IMemoryContradictionDetector
{
    /// <summary>
    /// Negation markers used to detect opposite valence on capability-claim statements.
    /// Matches a known intent ("cannot", "blocked", etc.) regardless of placement.
    /// </summary>
    [GeneratedRegex(
        @"\b(cannot|can't|cant|does not|doesn't|isn't|is not|are not|aren't|won't|will not|" +
        @"never|no longer|not supported|not exposed|does not expose|wrapper limitation|blocked|fails|broken|" +
        @"unable to|cannot pass|can not)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex NegationMarkerPattern();

    /// <summary>Affirmative-directive markers (feedback path).</summary>
    [GeneratedRegex(
        @"\b(always|prefer|use|do|please)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex AffirmativeDirectivePattern();

    /// <summary>Negative-directive markers (feedback path).</summary>
    [GeneratedRegex(
        @"\b(never|avoid|stop|don't|do not|dont|skip|disable)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex NegativeDirectivePattern();

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "to", "of", "for",
        "in", "on", "at", "by", "with", "is", "are", "was", "were", "be", "been",
        "being", "do", "does", "did", "have", "has", "had", "i", "you", "we", "they",
        "it", "this", "that", "these", "those", "as", "from", "into", "about",
        "always", "never", "prefer", "avoid", "stop", "don't", "dont", "do",
        "cannot", "can't", "cant", "not", "no", "use", "using", "skip", "disable",
        "please", "should", "would", "could", "may", "might", "will", "shall"
    };

    private const float MinFeedbackOverlap = 0.4f;

    private readonly ILongTermMemory _memory;
    private readonly ILogger<MemoryContradictionDetector> _logger;

    public MemoryContradictionDetector(
        ILongTermMemory memory,
        ILogger<MemoryContradictionDetector> logger)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _logger = logger;
    }

    public async Task<ContradictionResolution> ResolveAsync(
        MemoryEntry incoming, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (CapabilityClaimCategories.IsCapabilityClaim(incoming.Category))
            return await ResolveCapabilityClaimAsync(incoming, cancellationToken);

        if (FeedbackMemoryCategories.IsFeedbackMemory(incoming.Category))
            return await ResolveFeedbackAsync(incoming, cancellationToken);

        return ContradictionResolution.None;
    }

    // ── Capability-claim path ─────────────────────────────────────────────────

    private async Task<ContradictionResolution> ResolveCapabilityClaimAsync(
        MemoryEntry incoming, CancellationToken ct)
    {
        // Same (server, tool) is the join key. The capability-claim writer always builds
        // the category as claim/capability/{server}/{tool}, so we match on the full prefix.
        var category = incoming.Category!;
        var existing = await _memory.SearchAsync(
            new MemorySearchCriteria(Category: category, MaxResults: 200),
            ct);

        var incomingHasNegation = NegationMarkerPattern().IsMatch(incoming.Content);
        var candidates = new List<MemoryEntry>();
        foreach (var e in existing)
        {
            if (string.Equals(e.Id, incoming.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.SupersededBy is not null)
                continue;
            var existingHasNegation = NegationMarkerPattern().IsMatch(e.Content);
            if (existingHasNegation == incomingHasNegation)
                continue;
            candidates.Add(e);
        }

        if (candidates.Count == 0)
            return ContradictionResolution.None;

        // User-correction wins regardless of recency.
        var correction = candidates.FirstOrDefault(FeedbackMemoryCategories.IsUserCorrection);
        if (correction is not null)
        {
            _logger.LogInformation(
                "ContradictionDetector: incoming claim {IncomingId} superseded by user correction {ExistingId} ({Category})",
                incoming.Id, correction.Id, incoming.Category);
            return ContradictionResolution.UserCorrectionWins(correction.Id);
        }

        var loserIds = candidates.Select(c => c.Id).ToList();
        _logger.LogInformation(
            "ContradictionDetector: capability-claim {IncomingId} supersedes {Count} older entry/entries in {Category}: {Ids}",
            incoming.Id, loserIds.Count, incoming.Category, string.Join(", ", loserIds));
        return ContradictionResolution.NewerWins(loserIds);
    }

    // ── Feedback path ─────────────────────────────────────────────────────────

    private async Task<ContradictionResolution> ResolveFeedbackAsync(
        MemoryEntry incoming, CancellationToken ct)
    {
        // Match within the feedback subtree. We use the incoming entry's category (which
        // already starts with "feedback/...") so a directive in feedback/style only matches
        // other feedback/style entries — the design's "same rule subject" approximation.
        var existing = await _memory.SearchAsync(
            new MemorySearchCriteria(
                Category: FeedbackMemoryCategories.Prefix,
                MaxResults: 500),
            ct);

        var incomingValence = ClassifyDirective(incoming.Content);
        if (incomingValence == DirectiveValence.Ambiguous)
            return ContradictionResolution.None;

        var incomingTokens = TokenizeNonStopwords(incoming.Content);
        if (incomingTokens.Count < 2)
            return ContradictionResolution.None;

        var contradicted = new List<MemoryEntry>();
        foreach (var e in existing)
        {
            if (string.Equals(e.Id, incoming.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (e.SupersededBy is not null)
                continue;
            // Same subtree only — different categories under feedback/* are different rule subjects.
            if (!CategoriesShareSubtree(incoming.Category, e.Category))
                continue;

            var existingValence = ClassifyDirective(e.Content);
            if (existingValence == DirectiveValence.Ambiguous || existingValence == incomingValence)
                continue;

            var existingTokens = TokenizeNonStopwords(e.Content);
            if (existingTokens.Count < 2)
                continue;

            if (JaccardOverlap(incomingTokens, existingTokens) < MinFeedbackOverlap)
                continue;

            contradicted.Add(e);
        }

        // Phase 3 design: ambiguous matches skip auto-resolution. We treat "more than one
        // candidate where a user correction does NOT obviously win" as ambiguous.
        if (contradicted.Count == 0)
            return ContradictionResolution.None;

        var correction = contradicted.FirstOrDefault(FeedbackMemoryCategories.IsUserCorrection);
        if (correction is not null)
        {
            _logger.LogInformation(
                "ContradictionDetector: incoming feedback {IncomingId} superseded by user correction {ExistingId}",
                incoming.Id, correction.Id);
            return ContradictionResolution.UserCorrectionWins(correction.Id);
        }

        // If the incoming entry is itself a user correction, it wins over all matches.
        // Otherwise more than one non-correction match is ambiguous — defer to the dream sweep.
        if (!FeedbackMemoryCategories.IsUserCorrection(incoming) && contradicted.Count > 1)
        {
            _logger.LogInformation(
                "ContradictionDetector: feedback save {IncomingId} matched {Count} candidates with no correction — deferring to dream sweep",
                incoming.Id, contradicted.Count);
            return ContradictionResolution.None;
        }

        var ids = contradicted.Select(c => c.Id).ToList();
        _logger.LogInformation(
            "ContradictionDetector: feedback {IncomingId} supersedes {Count} older entry/entries: {Ids}",
            incoming.Id, ids.Count, string.Join(", ", ids));
        return ContradictionResolution.NewerWins(ids);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private enum DirectiveValence { Affirmative, Negative, Ambiguous }

    private static DirectiveValence ClassifyDirective(string content)
    {
        // Negation dominates: "never use X" is a negative directive, even though "use"
        // also matches the affirmative pattern (the action verb the directive operates on).
        // Affirmative is only chosen when no negative marker is present.
        if (NegativeDirectivePattern().IsMatch(content))
            return DirectiveValence.Negative;
        if (AffirmativeDirectivePattern().IsMatch(content))
            return DirectiveValence.Affirmative;
        return DirectiveValence.Ambiguous;
    }

    private static bool CategoriesShareSubtree(string? a, string? b)
    {
        if (a is null || b is null) return false;
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;

        // Allow feedback/from-user/style and feedback/from-agent/style to share the rule subject "style".
        var aLeaf = LeafSegment(a);
        var bLeaf = LeafSegment(b);
        return aLeaf.Length > 0 && string.Equals(aLeaf, bLeaf, StringComparison.OrdinalIgnoreCase);

        static string LeafSegment(string category)
        {
            var slash = category.LastIndexOf('/');
            return slash < 0 ? category : category[(slash + 1)..];
        }
    }

    private static HashSet<string> TokenizeNonStopwords(string content)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenPattern().Matches(content))
        {
            var token = m.Value;
            if (token.Length < 3) continue;
            if (Stopwords.Contains(token)) continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static float JaccardOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0f;
        var intersection = 0;
        foreach (var t in a)
            if (b.Contains(t)) intersection++;
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0f : (float)intersection / union;
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_'-]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 200)]
    private static partial Regex TokenPattern();
}
