using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// The weekly LLM-judged sample eval: takes a handful of decisions memory management actually
/// made and asks a model whether each one was right.
/// </summary>
/// <remarks>
/// <para>
/// The counters in a snapshot say what happened; they cannot say whether it was correct. A merge
/// that keeps every specific still passes the coverage check while producing prose that means
/// something different, and a corpus of clean-looking numbers is exactly what the store showed
/// through every incident so far. This is the only part of the audit that reads content.
/// </para>
/// <para>
/// Sampling is static and the call is gated on the corpus fingerprint, so the cost is a handful
/// of Balanced-tier JSON calls a week, and zero on a week where nothing changed. The judge sees
/// no tools — it is asked one question and answers it, exactly as every dream pass does.
/// </para>
/// </remarks>
internal sealed class MemoryAuditEvaluator(ILlmClient llm, ILogger logger)
{
    internal const string MergeCategory = "merge";
    internal const string NearDuplicateCategory = "near-duplicate";
    internal const string HighReinforcementCategory = "high-reinforcement";
    internal const string EphemeralArchiveCategory = "ephemeral-archive";

    /// <summary>Longest entry content rendered into a judge prompt.</summary>
    /// <remarks>
    /// This was once 600, which hid exactly what the judge was asked about: merged replacements
    /// are the longest entries in the store, and a 2,700-character merge was judged to have
    /// dropped details that sat past the cut. Real entries run to a few thousand characters and
    /// fit whole; the cap exists only so one runaway entry cannot swamp the call. The worst case
    /// is <see cref="MemoryAuditOptions.EvalSampleSize"/> merges, each a replacement plus a
    /// cluster of sources, at this size — bounded, and far above anything a healthy store holds.
    /// Anything cut is marked, and the prompt says so (see <see cref="TruncationNotice"/>).
    /// </remarks>
    internal const int MaxContentChars = 8_000;

    /// <summary>
    /// Told to the judge whenever an item in its call was cut, so content it cannot see is not
    /// read as content that was lost.
    /// </summary>
    internal static readonly string TruncationNotice =
        $"Some content below was cut at {MaxContentChars} characters and is marked [truncated]. " +
        "Do not report a detail as missing or lost merely because it is not visible past that point.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>One thing for the judge to rule on.</summary>
    /// <param name="Category">Which sampling family it came from.</param>
    /// <param name="Ids">Entry ids involved, carried through to the verdict so a finding is chaseable.</param>
    /// <param name="Text">Rendered prompt fragment describing the decision.</param>
    /// <param name="Truncated">
    /// Whether any entry in <paramref name="Text"/> was cut at <see cref="MaxContentChars"/>.
    /// </param>
    /// <param name="ContextIds">
    /// Live entries rendered beside the decision as evidence, kept apart from
    /// <paramref name="Ids"/> so nothing downstream reports them as affected.
    /// </param>
    internal sealed record Sample(
        string Category,
        IReadOnlyList<string> Ids,
        string Text,
        bool Truncated = false,
        IReadOnlyList<string>? ContextIds = null);

    private sealed record VerdictDto(int Index, bool Sound, string? Reason);

    private sealed record VerdictsDto(List<VerdictDto>? Verdicts);

    /// <summary>
    /// Picks what to judge. Deterministic given the same corpus: the most recent decisions in
    /// each family, capped at <see cref="MemoryAuditOptions.EvalSampleSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recency rather than randomness because the question being asked is "is memory management
    /// working <em>now</em>" — a random sample across a year would keep re-judging decisions
    /// made by code that has since been fixed.
    /// </para>
    /// <para>
    /// Near-duplicates are the exception: a pair left live is a failure however old it is, so they
    /// are not windowed. They are sampled one pair per cluster instead, rotating away from the
    /// clusters the previous eval judged — see <see cref="SelectNearDuplicatePairs"/>.
    /// </para>
    /// </remarks>
    /// <param name="vocabulary">
    /// Merge-coverage vocabulary, so the coverage line each merge sample carries reports what the
    /// dream's own coverage check would. <see langword="null"/> uses the built-in default.
    /// </param>
    /// <param name="previouslyJudgedIds">
    /// Entry ids the previous eval showed the judge as near-duplicates. Clusters touching any of
    /// them are sampled only once every other cluster has been. <see langword="null"/> means no
    /// previous eval.
    /// </param>
    /// <param name="liveNeighbours">
    /// The live entries most similar to each discard <see cref="SelectEphemeralDiscards"/> picks,
    /// keyed by discard id. <see langword="null"/> means live memory was not searched at all; a
    /// discard missing from a non-null map means its search failed. Either way the sample says
    /// so, rather than reading as a search that found nothing.
    /// </param>
    internal static IReadOnlyList<Sample> SelectSamples(
        IReadOnlyList<MemoryEntry> entries,
        IReadOnlyList<ShingleSimilarity.Pair> nearDupPairs,
        MemoryAuditOptions options,
        DateTimeOffset now,
        MergeCoverageVocabulary? vocabulary = null,
        IReadOnlySet<string>? previouslyJudgedIds = null,
        IReadOnlyDictionary<string, IReadOnlyList<MemorySimilarityMatch>>? liveNeighbours = null)
    {
        var byId = new Dictionary<string, MemoryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            byId[entry.Id] = entry;

        var cutoff = now - options.EvalWindow;
        var samples = new List<Sample>();

        // Merges made inside the window, newest first.
        var merges = entries
            .Where(e => e.ArchivedAt is null && MemoryAuditAnalyzer.MergedFromIds(e).Count > 0)
            .Where(e => (e.UpdatedAt ?? e.CreatedAt) >= cutoff)
            .OrderByDescending(e => e.UpdatedAt ?? e.CreatedAt)
            .Take(options.EvalSampleSize);

        foreach (var merge in merges)
        {
            var sources = MemoryAuditAnalyzer.MergedFromIds(merge)
                .Select(id => byId.GetValueOrDefault(id))
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();

            // A merge whose sources have all been purged cannot be judged — there is nothing
            // left to compare the result against, which is precisely why merges are archived
            // rather than deleted.
            if (sources.Count == 0) continue;

            var truncated = false;
            var text = new StringBuilder();
            text.AppendLine("Sources that were merged away:");
            foreach (var source in sources)
                text.AppendLine($"  - [{source.Id}] {Render(source.Content, ref truncated)}");
            text.AppendLine($"Replacement kept in memory: {Render(merge.Content, ref truncated)}");

            // Run over the full content, not the rendered text: the check is the one thing in the
            // prompt that can see past a cut.
            text.AppendLine(CoverageLine(MergeCoverage.FindMissingSpecifics(sources, merge.Content, vocabulary)));

            samples.Add(new Sample(
                MergeCategory,
                [merge.Id, .. sources.Select(s => s.Id)],
                text.ToString().TrimEnd(),
                truncated));
        }

        // Near-duplicate pairs still both live — deduplication that did not happen.
        foreach (var pair in SelectNearDuplicatePairs(nearDupPairs, byId, previouslyJudgedIds, options.EvalSampleSize))
        {
            var a = byId[pair.IdA];
            var b = byId[pair.IdB];

            var truncated = false;
            samples.Add(new Sample(
                NearDuplicateCategory,
                [a.Id, b.Id],
                $"Two entries both live in memory (lexical overlap {Pct(pair.Score)}):\n" +
                $"  - [{a.Id}] {Render(a.Content, ref truncated)}\n" +
                $"  - [{b.Id}] {Render(b.Content, ref truncated)}",
                truncated));
        }

        // Heavily reinforced entries — the corpus's load-bearing facts.
        var reinforced = entries
            .Where(e => e.ArchivedAt is null && e.ReinforcementCount >= options.HighReinforcementFloor)
            .OrderByDescending(e => e.ReinforcementCount)
            .Take(options.EvalSampleSize);

        foreach (var entry in reinforced)
        {
            var truncated = false;
            samples.Add(new Sample(
                HighReinforcementCategory,
                [entry.Id],
                $"Reinforced {entry.ReinforcementCount}x, importance {Score(entry.ImportanceScore)}, " +
                $"category {entry.Category ?? "(none)"}:\n  [{entry.Id}] {Render(entry.Content, ref truncated)}",
                truncated));
        }

        // Facts consolidation discarded outright, with nothing put in their place — shown with the
        // live entries most like them, since a discard is only a loss if none of those carry it.
        foreach (var entry in SelectEphemeralDiscards(entries, options, now))
        {
            var truncated = false;
            var text = new StringBuilder();
            text.AppendLine(
                $"Dropped as ephemeral on {entry.ArchivedAt:yyyy-MM-dd} " +
                $"(reinforced {entry.ReinforcementCount}x, importance {Score(entry.ImportanceScore)}):");
            text.AppendLine($"  [{entry.Id}] {Render(entry.Content, ref truncated)}");

            IReadOnlyList<MemorySimilarityMatch>? neighbours = null;
            if (liveNeighbours is null)
                text.AppendLine("Live memory was not searched for entries like this one.");
            else if (!liveNeighbours.TryGetValue(entry.Id, out neighbours))
                text.AppendLine("The search of live memory for entries like this one failed.");
            else if (neighbours.Count == 0)
                text.AppendLine("Most similar live entries: none found.");
            else
            {
                text.AppendLine($"Most similar live entries ({MeasureName(neighbours[0].Measure)} similarity):");
                foreach (var match in neighbours)
                    text.AppendLine(
                        $"  - [{match.Entry.Id}] ({Score((float)match.Score)}, " +
                        $"category {match.Entry.Category ?? "(none)"}) {Render(match.Entry.Content, ref truncated)}");
            }

            samples.Add(new Sample(
                EphemeralArchiveCategory,
                [entry.Id],
                text.ToString().TrimEnd(),
                truncated,
                neighbours is { Count: > 0 } ? [.. neighbours.Select(m => m.Entry.Id)] : null));
        }

        return samples;
    }

    /// <summary>
    /// The ephemeral discards the eval judges: archived as ephemeral inside the window, newest
    /// first, capped at <see cref="MemoryAuditOptions.EvalSampleSize"/>. The service searches live
    /// memory for exactly these, so it and <see cref="SelectSamples"/> cannot disagree.
    /// </summary>
    internal static IReadOnlyList<MemoryEntry> SelectEphemeralDiscards(
        IReadOnlyList<MemoryEntry> entries,
        MemoryAuditOptions options,
        DateTimeOffset now)
    {
        var cutoff = now - options.EvalWindow;
        return [.. entries
            .Where(e => e.ArchivedAt >= cutoff
                        && string.Equals(e.ArchiveReason, DreamService.EphemeralArchiveReason,
                            StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ArchivedAt)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Take(options.EvalSampleSize)];
    }

    private static string MeasureName(MemorySimilarityMeasure measure) => measure switch
    {
        MemorySimilarityMeasure.Embedding => "embedding",
        _ => "lexical"
    };

    /// <summary>
    /// Picks which near-duplicate pairs to judge: one per cluster, clusters the previous eval did
    /// not see first, capped at <paramref name="sampleSize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Taking the top pairs by score once re-judged the same ten pairs every week. Identical text
    /// scores 1.0 and always sorted first, and a cluster of <em>n</em> entries contributes
    /// n(n−1)/2 pairs, so four clusters filled the whole budget and nothing else in the corpus
    /// was ever looked at. Collapsing to clusters spreads the budget; deprioritising last week's
    /// clusters rotates it. They are deprioritised rather than excluded, so a small corpus still
    /// fills the budget instead of alternating between halves.
    /// </para>
    /// <para>
    /// Pairs the dream's exact-duplicate fold will collapse are dropped before clustering, using
    /// the fold's own predicates so the two can never disagree: judging them measures nothing.
    /// Identical text filed under different categories is not folded, so it stays sampleable.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<ShingleSimilarity.Pair> SelectNearDuplicatePairs(
        IReadOnlyList<ShingleSimilarity.Pair> pairs,
        IReadOnlyDictionary<string, MemoryEntry> byId,
        IReadOnlySet<string>? previouslyJudgedIds,
        int sampleSize)
    {
        var candidates = new List<(ShingleSimilarity.Pair Pair, MemoryEntry A, MemoryEntry B)>();
        foreach (var pair in pairs)
        {
            if (!byId.TryGetValue(pair.IdA, out var a) || !byId.TryGetValue(pair.IdB, out var b))
                continue;
            if (WillBeFolded(a, b))
                continue;

            candidates.Add((pair, a, b));
        }

        // Union-find over canonical entry ids.
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        string Find(string id)
        {
            parent.TryAdd(id, id);
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }
            return id;
        }

        foreach (var (_, a, b) in candidates)
        {
            var rootA = Find(a.Id);
            var rootB = Find(b.Id);
            if (rootA != rootB)
                parent[rootA] = rootB;
        }

        var judged = previouslyJudgedIds ?? new HashSet<string>();

        return [.. candidates
            .GroupBy(c => Find(c.A.Id), StringComparer.Ordinal)
            .Select(cluster =>
            {
                var best = cluster
                    .OrderByDescending(c => c.Pair.Score)
                    .ThenBy(c => c.A.Id, StringComparer.Ordinal)
                    .ThenBy(c => c.B.Id, StringComparer.Ordinal)
                    .First();
                var seen = cluster.Any(c => judged.Contains(c.A.Id) || judged.Contains(c.B.Id));
                return (best.Pair, Seen: seen, Key: MinOrdinal(best.A.Id, best.B.Id));
            })
            .OrderBy(c => c.Seen)
            .ThenByDescending(c => c.Pair.Score)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .Take(sampleSize)
            .Select(c => c.Pair)];
    }

    private static bool WillBeFolded(MemoryEntry a, MemoryEntry b) =>
        DreamService.IsExactDuplicateEligible(a)
        && DreamService.IsExactDuplicateEligible(b)
        && DreamService.ExactDuplicateKey(a) == DreamService.ExactDuplicateKey(b);

    private static string MinOrdinal(string x, string y) =>
        string.CompareOrdinal(x, y) <= 0 ? x : y;

    /// <summary>
    /// Judges <paramref name="samples"/>, one LLM call per category. A category whose call fails
    /// or returns unparseable JSON is left out of the result rather than defaulting to sound —
    /// an eval that scores itself in the absence of an answer is worse than no eval.
    /// </summary>
    internal async Task<MemoryAuditEvalResult?> EvaluateAsync(
        IReadOnlyList<Sample> samples,
        string directive,
        ModelTier tier,
        string storeFingerprint,
        CancellationToken ct)
    {
        if (samples.Count == 0) return null;

        var verdicts = new List<MemoryAuditEvalVerdict>();

        foreach (var group in samples.GroupBy(s => s.Category))
        {
            ct.ThrowIfCancellationRequested();

            var items = group.ToList();
            var judged = await JudgeAsync(group.Key, items, directive, tier, ct).ConfigureAwait(false);
            if (judged is null) continue;

            verdicts.AddRange(judged);
        }

        if (verdicts.Count == 0) return null;

        var rateByCategory = verdicts
            .GroupBy(v => v.Category)
            .ToDictionary(g => g.Key, g => (double)g.Count(v => v.Sound) / g.Count(), StringComparer.Ordinal);

        var sound = verdicts.Count(v => v.Sound);

        var summary = new MemoryAuditEvalSummary(
            DateTimeOffset.UtcNow,
            verdicts.Count,
            sound,
            (double)sound / verdicts.Count,
            rateByCategory);

        return new MemoryAuditEvalResult(summary, verdicts, storeFingerprint);
    }

    private async Task<List<MemoryAuditEvalVerdict>?> JudgeAsync(
        string category,
        List<Sample> items,
        string directive,
        ModelTier tier,
        CancellationToken ct)
    {
        var userMessage = new StringBuilder();
        userMessage.AppendLine($"Decision family: {category}");
        userMessage.AppendLine($"{Question(category)}");
        // Carried in the user message rather than only the directive: memory-audit.md on a
        // deployed profile volume is never overwritten by an image upgrade.
        if (items.Any(i => i.Truncated))
            userMessage.AppendLine(TruncationNotice);
        userMessage.AppendLine();
        for (var i = 0; i < items.Count; i++)
        {
            userMessage.AppendLine($"{i + 1}.");
            userMessage.AppendLine(items[i].Text);
            userMessage.AppendLine();
        }
        userMessage.AppendLine(
            "Answer with JSON: {\"verdicts\":[{\"index\":1,\"sound\":true,\"reason\":\"one short sentence\"}]}. " +
            "Include one object per numbered item.");

        try
        {
            var response = await llm.GetResponseAsync(
                [new ChatMessage(ChatRole.System, directive),
                 new ChatMessage(ChatRole.User, userMessage.ToString())],
                tier,
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json },
                ct).ConfigureAwait(false);

            var json = DreamService.ExtractJsonObject(response.Text?.Trim() ?? string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                logger.LogWarning("Memory audit: eval judge returned no parseable JSON for {Category}", category);
                return null;
            }

            var dto = JsonSerializer.Deserialize<VerdictsDto>(json, JsonOptions);
            if (dto?.Verdicts is not { Count: > 0 })
            {
                logger.LogWarning("Memory audit: eval judge returned no verdicts for {Category}", category);
                return null;
            }

            var results = new List<MemoryAuditEvalVerdict>();
            foreach (var verdict in dto.Verdicts)
            {
                // The index is the model's only handle on which item it is talking about, so an
                // out-of-range one is dropped rather than attributed to the wrong entries.
                if (verdict.Index < 1 || verdict.Index > items.Count) continue;
                var sample = items[verdict.Index - 1];
                results.Add(new MemoryAuditEvalVerdict(
                    sample.Category, sample.Ids, verdict.Sound, verdict.Reason?.Trim(), sample.ContextIds));
            }

            return results.Count > 0 ? results : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Memory audit: eval judge failed for {Category}", category);
            return null;
        }
    }

    /// <summary>
    /// Hash of the live corpus plus the archive size. Two runs with the same fingerprint would
    /// judge exactly the same decisions, so the second one skips the call entirely.
    /// </summary>
    internal static string StoreFingerprint(IReadOnlyList<MemoryEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var id in entries.Where(e => e.ArchivedAt is null)
                     .Select(e => e.Id)
                     .OrderBy(id => id, StringComparer.Ordinal))
            sb.Append(id).Append('\u001f');  // unit separator: cannot occur in an id

        sb.Append("archived=").Append(entries.Count(e => e.ArchivedAt is not null));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())).AsSpan(0, 16));
    }

    /// <summary>
    /// The one statement of which way <c>sound</c> points for the near-duplicate family. The
    /// per-family question, <see cref="BuiltInDirective"/> and the shipped <c>memory-audit.md</c>
    /// must all contain it.
    /// </summary>
    /// <remarks>
    /// They once disagreed: the question said a duplicate was sound=false and the directive said
    /// sound=true, so the family's sound rate meant whichever instruction the judge happened to
    /// weigh more. Every family reads <c>sound</c> as "memory management made the right call",
    /// and leaving a genuine duplicate live is the wrong one.
    /// </remarks>
    internal const string NearDuplicatePolarity =
        "genuine duplicates that should have been folded together are NOT sound";

    /// <summary>
    /// The one statement of when an ephemeral discard counts as a loss. The per-family question,
    /// <see cref="BuiltInDirective"/> and the shipped <c>memory-audit.md</c> must all contain it.
    /// </summary>
    /// <remarks>
    /// The judge once saw each discard alone and was asked only whether it named a durable fact,
    /// so a discard restating a fact still held by a live entry under another category was
    /// reported as lost. The family scored 50% on decisions that were almost all right.
    /// </remarks>
    internal const string EphemeralSurvivalRule =
        "a discarded fact that a live entry shown beside it still carries was NOT lost";

    internal static string Question(string category) => category switch
    {
        MergeCategory =>
            "Did the replacement preserve everything the sources said that a reader would need? " +
            "Answer sound=false if any name, date, number, qualifier or distinction was lost or altered.",
        NearDuplicateCategory =>
            "Do these two entries state the same fact, such that keeping both is redundant? " +
            $"Leaving both live was the wrong call, so {NearDuplicatePolarity}: answer sound=false " +
            "for a genuine duplicate, and sound=true for distinct facts that merely look similar.",
        HighReinforcementCategory =>
            "Is this entry still a coherent, specific, useful fact? Answer sound=false if repeated " +
            "reinforcement has turned it into a vague or self-contradictory blob.",
        EphemeralArchiveCategory =>
            "Was this safe to discard, given the live entries shown beside it? " +
            $"Nothing is lost while memory still holds it elsewhere, so {EphemeralSurvivalRule}. " +
            "Answer sound=false only if it names a durable fact, preference, commitment or identity " +
            "detail that appears in none of the live entries shown; a passing detail is always sound " +
            "to discard. Where live memory was not searched, judge the entry on its own.",
        _ => "Was this the right outcome?"
    };

    /// <summary>
    /// Percentage and score formatting pinned to the invariant culture. See
    /// <c>MemoryAuditReportWriter.Percent</c> — "P0" renders as "75 %" under the culture the
    /// container runs with, and an importance score of "0,97" reads as a different number.
    /// </summary>
    private static string Pct(double fraction) =>
        (fraction * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private static string Score(float value) =>
        value.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Flattens <paramref name="text"/> to one line and cuts it at <see cref="MaxContentChars"/>,
    /// marking the cut with how much was shown. Sets <paramref name="truncated"/> when it cuts,
    /// and never clears it, so one flag can cover every entry in a sample.
    /// </summary>
    private static string Render(string? text, ref bool truncated)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        var flat = text.ReplaceLineEndings(" ").Trim();
        if (flat.Length <= MaxContentChars) return flat;

        truncated = true;
        return $"{flat[..MaxContentChars]}… [truncated: showed {MaxContentChars} of {flat.Length} chars]";
    }

    /// <summary>
    /// The merge sample's report of <see cref="MergeCoverage.FindMissingSpecifics"/>. Worded as
    /// a verbatim string check rather than a ruling: the check is conservative, so a specific the
    /// replacement reworded is listed too, and whether the meaning survived is the judge's call.
    /// </summary>
    internal static string CoverageLine(IReadOnlyList<string> missing) =>
        missing.Count == 0
            ? "Coverage check: every name, number and date in the sources appears verbatim in the replacement."
            : "Coverage check: these source specifics do not appear verbatim in the replacement: " +
              string.Join(", ", missing);

    /// <summary>
    /// Fallback judge directive, used when <c>memory-audit.md</c> is absent from the profile
    /// volume. Every other dream pass carries one for the same reason: a missing file must
    /// degrade to the built-in behaviour, never to silence.
    /// </summary>
    internal const string BuiltInDirective = $$"""
        You are auditing an AI agent's long-term memory. You are shown decisions the memory
        system already made — merges it performed, duplicates it left in place, facts it
        discarded, entries it has reinforced many times — and asked whether each was correct.

        You are a reviewer, not an editor. Do not propose rewrites, do not suggest merges, and
        do not comment on style. Answer only whether the stored outcome was right.

        Judge conservatively in the direction of keeping information:
        - A merge that dropped a name, date, number, or distinction is NOT sound, however
          tidier the result reads.
        - A discarded entry that named a durable fact, preference, commitment, or identity
          detail is NOT sound, unless memory still holds that fact: {{EphemeralSurvivalRule}}
          (sound=true). Check the most similar live entries shown with each discard before
          calling it a loss. Genuinely passing details (a one-off status, a transient
          scheduling note) are sound to discard.
        - Two entries stating the same fact in different words ARE duplicates, even if the
          wording shares few tokens. Leaving both live was the wrong call, so
          {{NearDuplicatePolarity}} (sound=false). Distinct facts that merely look similar are
          sound (sound=true).
        - An entry reinforced many times that has become vague, generic, or self-contradictory
          is NOT sound, even though nothing was formally lost.

        Content cut for length ends in a [truncated] marker. A detail you cannot see past that
        point is not a detail that was lost. A merge's "Coverage check" line is a verbatim string
        comparison over the full text: use it as evidence, but a specific the replacement
        reworded without losing its meaning is still kept.

        Reply with JSON only: {"verdicts":[{"index":1,"sound":true,"reason":"..."}]}
        One object per numbered item, in any order. Keep each reason to one short sentence.
        """;
}
