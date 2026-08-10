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

    private readonly HashSet<string> _common;
    private readonly HashSet<string> _alwaysSpecific;

    public MergeCoverageVocabulary(
        IEnumerable<string>? extraCommonWords,
        IEnumerable<string>? alwaysSpecificWords)
    {
        _common = new HashSet<string>(BuiltInCommonWords, StringComparer.OrdinalIgnoreCase);
        _alwaysSpecific = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in extraCommonWords ?? [])
            if (!string.IsNullOrWhiteSpace(word))
                _common.Add(word.Trim());

        foreach (var word in alwaysSpecificWords ?? [])
            if (!string.IsNullOrWhiteSpace(word))
                _alwaysSpecific.Add(word.Trim());
    }

    /// <summary>
    /// True when <paramref name="word"/> should be ignored rather than required to survive a
    /// merge. <see cref="AlwaysSpecificWords"/> wins over the common list, so a deployment can
    /// reclaim a built-in word it needs protected.
    /// </summary>
    public bool IsCommon(string word) =>
        !_alwaysSpecific.Contains(word) && _common.Contains(word);

    /// <summary>Words reclaimed as specifics regardless of the common list.</summary>
    public IReadOnlyCollection<string> AlwaysSpecificWords => _alwaysSpecific;

    /// <summary>Count of words treated as ordinary language.</summary>
    public int CommonWordCount => _common.Count;

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
    ];
}
