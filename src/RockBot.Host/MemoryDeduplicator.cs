using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RockBot.Host;

/// <summary>
/// Default <see cref="IMemoryDeduplicator"/>: looks for the live entry a candidate most
/// resembles and, when the resemblance is close enough, reinforces or extends it instead of
/// writing a near-copy.
/// </summary>
/// <remarks>
/// <para>
/// The decision is deliberately narrow. Similarity alone only says the two entries are about the
/// same thing; whether the existing one can stand in for the new one is the same question dream
/// consolidation asks about a merge, so it is answered by the same code —
/// <see cref="MergeCoverage.FindMissingSpecifics"/> over the same vocabulary file. If the
/// existing entry already carries every specific, the candidate is evidence and nothing more. If
/// it carries new ones, they are appended rather than dropped: a save that silently discards a
/// detail is the failure this whole area exists to prevent, and it would be far harder to notice
/// here than in a merge, because there would be no second entry to compare against.
/// </para>
/// <para>
/// Anything below the threshold, or in a category with its own write semantics, is saved
/// unchanged. Consolidation still runs; this only stops it from being handed duplicates it never
/// needed to see.
/// </para>
/// </remarks>
internal sealed class MemoryDeduplicator : IMemoryDeduplicator
{
    private readonly ILongTermMemory _memory;
    private readonly IMemorySimilarityLookup? _lookup;
    private readonly MemoryOptions _options;
    private readonly AgentProfileOptions _profileOptions;
    private readonly DreamOptions? _dreamOptions;
    private readonly ILogger<MemoryDeduplicator> _logger;

    /// <summary>
    /// Serializes lookup-then-write. Two background saves of the same fact — two turns
    /// mentioning it, or a mining pass overlapping a tool call — would otherwise both look,
    /// both find nothing, and both create.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MergeCoverageVocabulary _vocabulary = MergeCoverageVocabulary.Default;
    private DateTime _vocabularyStamp = DateTime.MinValue;
    private bool _vocabularyLoaded;
    private bool _loggedMissingLookup;

    public MemoryDeduplicator(
        ILongTermMemory memory,
        IOptions<MemoryOptions> options,
        IOptions<AgentProfileOptions> profileOptions,
        ILogger<MemoryDeduplicator> logger,
        IOptions<DreamOptions>? dreamOptions = null)
    {
        _memory = memory;
        _lookup = memory as IMemorySimilarityLookup;
        _options = options.Value;
        _profileOptions = profileOptions.Value;
        _dreamOptions = dreamOptions?.Value;
        _logger = logger;
    }

    public async Task<MemorySaveOutcome> SaveOrReinforceAsync(
        MemoryEntry candidate,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldDeduplicate(candidate))
        {
            await _memory.SaveAsync(candidate, cancellationToken);
            return new MemorySaveOutcome(MemorySaveAction.Created, candidate.Id);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await DecideAndSaveAsync(candidate, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ShouldDeduplicate(MemoryEntry candidate)
    {
        if (_lookup is null)
        {
            if (!_loggedMissingLookup)
            {
                _loggedMissingLookup = true;
                _logger.LogDebug(
                    "Memory dedupe: store does not support similarity lookup; saving every entry as-is");
            }
            return false;
        }

        if (!_options.DedupeEnabled)
            return false;

        if (string.IsNullOrWhiteSpace(candidate.Content))
            return false;

        // An entry that arrives already superseded is a record of a resolved contradiction, not
        // a fact being asserted; folding it into its own winner would erase the audit trail.
        if (candidate.SupersededBy is not null)
            return false;

        // Scoped categories have their own write semantics — the contradiction detector
        // supersedes feedback entries, and capability claims are falsified and evicted by the
        // verifier. Both reason about individual entries by id, so neither wants its writes
        // silently redirected into an older one. Defensive rather than expected: mining can and
        // does emit entries under these prefixes.
        if (FeedbackMemoryCategories.IsFeedbackMemory(candidate.Category)
            || CapabilityClaimCategories.IsCapabilityClaim(candidate.Category))
            return false;

        return true;
    }

    private async Task<MemorySaveOutcome> DecideAndSaveAsync(
        MemoryEntry candidate,
        CancellationToken cancellationToken)
    {
        var match = await _lookup!.FindMostSimilarAsync(candidate, cancellationToken);

        var threshold = match?.Measure == MemorySimilarityMeasure.Embedding
            ? _options.DedupeSimilarityThreshold
            : _options.DedupeLexicalSimilarityThreshold;

        if (match is null || match.Score < threshold)
        {
            if (match is not null)
                _logger.LogDebug(
                    "Memory dedupe: saving {Id} as new — closest existing entry {MatchId} scored " +
                    "{Score:F2} ({Measure}), below the {Threshold:F2} threshold",
                    candidate.Id, match.Entry.Id, match.Score, match.Measure, threshold);

            await _memory.SaveAsync(candidate, cancellationToken);
            return new MemorySaveOutcome(MemorySaveAction.Created, candidate.Id);
        }

        var existing = match.Entry;
        var missing = MergeCoverage.FindMissingSpecifics(
            [candidate], existing.Content, LoadVocabulary());

        var now = DateTimeOffset.UtcNow;
        var reinforced = Reinforce(existing, candidate, now);

        if (missing.Count == 0)
        {
            await _memory.SaveAsync(reinforced, cancellationToken);

            _logger.LogInformation(
                "Memory dedupe: reinforced {Id} instead of creating (similarity {Score:F2}, " +
                "{Measure}, reinforced={Count}×): {Content}",
                existing.Id, match.Score, match.Measure, reinforced.ReinforcementCount, existing.Content);

            return new MemorySaveOutcome(
                MemorySaveAction.Reinforced, existing.Id, match.Score, match.Measure);
        }

        var addition = candidate.Content.Trim();
        var kept = existing.Content.TrimEnd();
        var combinedLength = kept.Length + 2 + addition.Length;

        if (combinedLength > _options.DedupeMaxExtendedContentLength)
        {
            _logger.LogInformation(
                "Memory dedupe: saving {Id} as new — extending {MatchId} would reach {Length} characters, " +
                "over the {Cap} cap (similarity {Score:F2}, {Measure})",
                candidate.Id, existing.Id, combinedLength,
                _options.DedupeMaxExtendedContentLength, match.Score, match.Measure);

            await _memory.SaveAsync(candidate, cancellationToken);
            return new MemorySaveOutcome(MemorySaveAction.Created, candidate.Id);
        }

        // UpdatedAt moves here and not on the reinforce path: the content changed, so the
        // consolidation-reviewed hash no longer matches and the entry is rightly re-reviewed
        // next cycle. A pure reinforcement changes no text and stays withheld.
        var extended = reinforced with
        {
            Content = kept + "\n\n" + addition,
            UpdatedAt = now,
        };

        await _memory.SaveAsync(extended, cancellationToken);

        _logger.LogInformation(
            "Memory dedupe: extended {Id} with {Specifics} instead of creating (similarity {Score:F2}, {Measure})",
            existing.Id, string.Join(", ", missing), match.Score, match.Measure);

        return new MemorySaveOutcome(
            MemorySaveAction.Extended, existing.Id, match.Score, match.Measure);
    }

    /// <summary>
    /// Folds the candidate's evidence into <paramref name="existing"/> without touching its
    /// text: counters, importance, tags and any metadata keys the candidate brought that the
    /// existing entry lacks.
    /// </summary>
    /// <remarks>
    /// <see cref="MemoryEntry.UpdatedAt"/> is deliberately left alone. It anchors the importance
    /// decay pass and, through the content hash, the consolidation-reviewed stamp; bumping it
    /// for a write that changed no text would both stall decay and re-open a settled entry.
    /// Leaving it also keeps the store's UpdatedAt-regression warning from firing on what is a
    /// read-modify-write outside its lock.
    /// </remarks>
    internal static MemoryEntry Reinforce(MemoryEntry existing, MemoryEntry candidate, DateTimeOffset now)
    {
        var tags = new List<string>(existing.Tags);
        foreach (var tag in candidate.Tags)
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);

        IReadOnlyDictionary<string, string>? metadata = existing.Metadata;
        if (candidate.Metadata is { Count: > 0 })
        {
            var merged = existing.Metadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(existing.Metadata, StringComparer.OrdinalIgnoreCase);

            // Existing values win. The candidate is the same fact observed again, so its
            // metadata is a second reading of what is already recorded, not a correction.
            foreach (var (key, value) in candidate.Metadata)
                if (!merged.ContainsKey(key))
                    merged[key] = value;

            metadata = merged;
        }

        return existing with
        {
            Tags = tags,
            Metadata = metadata,
            ImportanceScore = Math.Max(existing.ImportanceScore, candidate.ImportanceScore),
            LastSeenAt = now,
            ReinforcementCount = existing.ReinforcementCount + 1,
        };
    }

    /// <summary>
    /// Returns the merge-coverage vocabulary, re-reading the file when it has changed on disk.
    /// </summary>
    /// <remarks>
    /// The same file dream consolidation reloads at the top of every cycle. Both now decide what
    /// counts as a load-bearing specific, and an operator who tunes the file to unblock a merge
    /// would be confused to find the save path still judging by the old rules.
    /// </remarks>
    private MergeCoverageVocabulary LoadVocabulary()
    {
        var path = ResolveVocabularyPath();

        DateTime stamp;
        try
        {
            stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (IOException)
        {
            return _vocabularyLoaded ? _vocabulary : MergeCoverageVocabulary.Default;
        }

        if (_vocabularyLoaded && stamp == _vocabularyStamp)
            return _vocabulary;

        _vocabulary = MergeCoverageVocabularyFile.Load(path, _logger, source: "Memory dedupe");
        _vocabularyStamp = stamp;
        _vocabularyLoaded = true;
        return _vocabulary;
    }

    private string ResolveVocabularyPath()
    {
        var configured = _dreamOptions?.MergeCoverageVocabularyPath ?? "merge-coverage-vocabulary.json";
        if (Path.IsPathRooted(configured))
            return configured;

        var baseDir = Path.IsPathRooted(_profileOptions.BasePath)
            ? _profileOptions.BasePath
            : Path.Combine(AppContext.BaseDirectory, _profileOptions.BasePath);

        return Path.Combine(baseDir, configured);
    }
}
