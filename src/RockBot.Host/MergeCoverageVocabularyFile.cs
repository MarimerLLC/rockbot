using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Reads <c>merge-coverage-vocabulary.json</c> from the agent profile volume.
/// </summary>
/// <remarks>
/// Shared rather than owned by <see cref="DreamService"/> because two passes now judge the same
/// question. Consolidation asks "does this merge still carry the sources' specifics?"; save-time
/// deduplication asks "does the entry I already have still carry this new one's specifics?" —
/// the same coverage test over the same vocabulary. Loading the file twice from two copies of
/// the same block is how the two would quietly drift apart.
/// </remarks>
internal static class MergeCoverageVocabularyFile
{
    /// <summary>
    /// Loads the vocabulary at <paramref name="path"/>, falling back to
    /// <see cref="MergeCoverageVocabulary.Default"/> when the file is absent or malformed.
    /// </summary>
    /// <param name="path">Resolved path to the vocabulary file.</param>
    /// <param name="logger">Where load and parse outcomes are reported.</param>
    /// <param name="source">
    /// Name of the component doing the loading, used as the log-line prefix so an operator can
    /// tell which reader picked their edit up.
    /// </param>
    public static MergeCoverageVocabulary Load(string path, ILogger logger, string source = "DreamService")
    {
        if (!File.Exists(path))
            return MergeCoverageVocabulary.Default;

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex,
                "{Source}: could not read {Path}; using the built-in vocabulary", source, path);
            return MergeCoverageVocabulary.Default;
        }

        var vocabulary = MergeCoverageVocabulary.Parse(json, out var error);

        if (error is not null)
        {
            logger.LogWarning(
                "{Source}: {Path} is malformed ({Error}); using the built-in vocabulary",
                source, path, error);
            return vocabulary;
        }

        // Information, matching the per-cycle directive reload: this file decides what a merge
        // is allowed to drop, and an operator tuning it needs to see that their edit was picked
        // up without raising the whole namespace to Debug.
        logger.LogInformation(
            "{Source}: merge-coverage vocabulary from {Path} — {Common} common words, " +
            "{Specific} reclaimed as specifics{Reclaimed}, {NumericExempt} numeric-exempt categor(ies)",
            source,
            path,
            vocabulary.CommonWordCount,
            vocabulary.AlwaysSpecificWords.Count,
            vocabulary.AlwaysSpecificWords.Count > 0
                ? " (" + string.Join(", ", vocabulary.AlwaysSpecificWords) + ")"
                : string.Empty,
            vocabulary.NumericExemptCategories.Count);

        return vocabulary;
    }
}
