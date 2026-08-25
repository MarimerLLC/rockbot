using System.Text.Json;
using System.Text.Json.Serialization;

namespace RockBot.Host;

/// <summary>
/// Decides which words the merge coverage check treats as ordinary language rather than as
/// load-bearing specifics. Deployment-specific, because vocabulary is.
/// </summary>
/// <remarks>
/// <para>
/// The built-in list is generic English — words that appear capitalized only because they open
/// a sentence. That is a reasonable default but it is not portable, in both directions:
/// </para>
/// <list type="bullet">
/// <item>
/// An operational assistant accumulates its own noise words. Words that read as generic can
/// also be load-bearing — "Personal", "Class", "Benefit" and "Extended" name real things in one
/// live corpus ("OneDrive Personal", "Blazor Online Class", "MVP Azure Extended Benefit"), so
/// suppressing them would blunt a correct rejection.
/// </item>
/// <item>
/// A storytelling agent's characters collide head-on with the built-in list. A character named
/// May, Will, Rose, Grace or Hope would be silently stripped of coverage protection, which is
/// exactly the population that must never be lost. <see cref="AlwaysSpecificWords"/> exists for
/// that case and takes precedence over everything else.
/// </item>
/// </list>
/// <para>
/// Loaded from <c>merge-coverage-vocabulary.json</c> on the agent profile volume, alongside
/// <c>tier-selector.json</c> and the dream directives, and re-read at the top of every cycle so
/// edits take effect without a restart.
/// </para>
/// </remarks>
internal sealed class MergeCoverageVocabulary
{
    /// <summary>Generic English baseline, used when no vocabulary file is present.</summary>
    /// <remarks>
    /// Deferred rather than a plain initializer: static fields initialize in declaration order,
    /// and this is declared above the word list it is built from.
    /// </remarks>
    public static MergeCoverageVocabulary Default => LazyDefault.Value;

    private static readonly Lazy<MergeCoverageVocabulary> LazyDefault = new(() => new(null, null));

    private readonly HashSet<string> _builtIn;
    private readonly HashSet<string> _extraCommon;
    private readonly HashSet<string> _alwaysSpecific;

    public MergeCoverageVocabulary(
        IEnumerable<string>? extraCommonWords,
        IEnumerable<string>? alwaysSpecificWords)
    {
        _builtIn = new HashSet<string>(BuiltInCommonWords, StringComparer.OrdinalIgnoreCase);
        _extraCommon = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _alwaysSpecific = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in extraCommonWords ?? [])
            if (!string.IsNullOrWhiteSpace(word))
                _extraCommon.Add(word.Trim());

        foreach (var word in alwaysSpecificWords ?? [])
            if (!string.IsNullOrWhiteSpace(word))
                _alwaysSpecific.Add(word.Trim());
    }

    /// <summary>
    /// True when <paramref name="word"/> should be ignored rather than required to survive a
    /// merge. <see cref="AlwaysSpecificWords"/> wins over both lists, so a deployment can
    /// reclaim a built-in word it needs protected.
    /// </summary>
    /// <param name="applyBaseline">
    /// Whether the generic-English baseline applies here. False for a capitalized word in
    /// mid-sentence position, where the capitalization is evidence of a proper noun rather
    /// than of grammar — see <see cref="SentencePosition"/>.
    /// </param>
    /// <remarks>
    /// The two lists are deliberately scoped differently. The baseline is generic English that
    /// no operator chose: it contains "may", "will", "some", "first" and "last", so applying it
    /// mid-sentence is what would strip a character named May or Will of protection, and what
    /// forced "Personal", "Class" and "Benefit" to be left out of it despite reading as noise.
    /// <c>extraCommonWords</c> is the opposite — an explicit, corpus-specific judgement — so it
    /// applies in every position. That is what lets a deployment suppress framing noise such as
    /// "Rocky", which appears mid-sentence far more often than not.
    /// </remarks>
    public bool IsCommon(string word, bool applyBaseline = true)
    {
        if (_alwaysSpecific.Contains(word))
            return false;

        return _extraCommon.Contains(word)
            || (applyBaseline && _builtIn.Contains(word));
    }

    /// <summary>Words reclaimed as specifics regardless of the common list.</summary>
    public IReadOnlyCollection<string> AlwaysSpecificWords => _alwaysSpecific;

    /// <summary>Count of words treated as ordinary language.</summary>
    public int CommonWordCount => _builtIn.Count + _extraCommon.Count;

    /// <summary>
    /// Parses a vocabulary file. Returns <see cref="Default"/> when <paramref name="json"/> is
    /// absent or unusable — a malformed override must not disable coverage checking.
    /// </summary>
    public static MergeCoverageVocabulary Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
            return Default;

        try
        {
            var dto = JsonSerializer.Deserialize<VocabularyDto>(json, JsonOptions);
            if (dto is null)
            {
                error = "file parsed to null";
                return Default;
            }

            return new MergeCoverageVocabulary(dto.ExtraCommonWords, dto.AlwaysSpecificWords);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return Default;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record VocabularyDto(
        [property: JsonPropertyName("extraCommonWords")] string[]? ExtraCommonWords,
        [property: JsonPropertyName("alwaysSpecificWords")] string[]? AlwaysSpecificWords);

    /// <summary>
    /// Words that appear capitalized only by virtue of opening a sentence or labelling a
    /// clause. Generic English plus a few observed on live corpora; anything corpus-specific
    /// belongs in the vocabulary file, not here.
    /// </summary>
    private static readonly string[] BuiltInCommonWords =
    [
        "the", "this", "that", "these", "those", "there", "then", "than", "and", "but", "not",
        "are", "was", "were", "has", "have", "had", "its", "his", "her", "their", "they", "them",
        "also", "only", "just", "such", "each", "both", "all", "any", "one", "two", "three",
        "new", "now", "use", "uses", "used", "using", "should", "must", "may", "can", "will",
        "would", "could", "note", "example", "include", "includes", "including", "prefer",
        "prefers", "avoid", "does", "did", "set", "get", "save", "send", "read", "write", "run",
        "when", "what", "where", "while", "with", "from", "for", "into", "over", "under",
        "about", "after", "before", "during", "between", "because", "however", "instead",
        "since", "until", "upon", "user", "agent", "detail", "details", "task", "tasks", "item",
        "items", "entry", "entries", "memory", "working", "long", "term", "data", "time",
        "date", "day", "days", "week", "weeks", "month", "months", "year", "years", "ago",
        "per", "via", "context", "current", "currently", "still", "already", "always", "never",
        "other", "another", "same", "different", "first", "second", "last", "next", "previous",
        "some", "most", "many", "much", "more", "less", "very", "rather", "quite",
        "adding", "candidate", "candidates", "flagged", "validated", "recurring", "repeated",
        "correct", "corrected", "short", "topic", "topics", "attempts", "attempted",
        "recommend", "recommended", "confirmed", "verified", "known", "unknown", "successful",
        "failed", "failing", "pending", "active", "inactive", "enabled", "disabled",
        "created", "updated", "deleted", "removed", "added", "changed", "applied",
        "review", "reviewed", "summary", "status", "result", "results", "reason", "reasons",
        "issue", "issues", "error", "errors", "warning", "warnings", "notes", "given",
        "based", "across", "within", "without", "although", "though", "unless",
        "overall", "specifically", "particularly", "typically", "generally", "likely",
        "ids", "enjoys", "downloading",

        // Sentence openers observed rejecting sound merges on a live corpus. Adding these was
        // previously unsafe because the list applied in every position; now that the baseline
        // is consulted only at a sentence start, a word here still carries full protection
        // mid-phrase. Deliberately not extended to "personal", "class", "benefit", "extended",
        // "power", "social" or "code" — those are already protected where they are load-bearing
        // ("OneDrive Personal", "Blazor Online Class"), so listing them buys nothing.
        "valid", "invalid", "direct", "directly", "alternative", "alternatively",
        "through", "throughout", "call", "calls", "multiple", "relevant", "existing",
        "lowering", "raising", "several", "additional", "overdue", "upcoming",
    ];
}
