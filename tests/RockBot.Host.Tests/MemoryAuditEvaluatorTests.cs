using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace RockBot.Host.Tests;

/// <summary>
/// Sampling and verdict parsing for the weekly judged eval. The judge itself is a stub — what
/// matters here is that the right decisions are put in front of it and its answers come back
/// attached to the right entry ids.
/// </summary>
[TestClass]
public class MemoryAuditEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 5, 0, 0, TimeSpan.Zero);

    // ── Near-duplicate polarity ──────────────────────────────────────────────
    //
    // The question and the directive once told the judge opposite things about which way
    // `sound` points for a duplicate, so the family's sound rate depended on which instruction
    // the model weighed more. All three texts the judge can see must say the same thing.

    [TestMethod]
    [DataRow("question")]
    [DataRow("built-in directive")]
    [DataRow("memory-audit.md")]
    public void EveryJudgeTextStatesTheNearDuplicatePolarity(string source)
    {
        StringAssert.Contains(
            Normalize(JudgeText(source)),
            Normalize(MemoryAuditEvaluator.NearDuplicatePolarity),
            $"The {source} must say that a genuine duplicate left live is not sound.");
    }

    [TestMethod]
    [DataRow("question")]
    [DataRow("built-in directive")]
    [DataRow("memory-audit.md")]
    public void NoJudgeTextCallsAGenuineDuplicateSound(string source)
    {
        var offending = Sentences(JudgeText(source))
            .Where(s => s.Contains("sound=true", StringComparison.OrdinalIgnoreCase)
                && (s.Contains("genuinely duplicates", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("should have been folded", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.AreEqual(0, offending.Count,
            $"The {source} ties sound=true to a genuine duplicate: {string.Join(" | ", offending)}");
    }

    // ── Ephemeral survival rule ──────────────────────────────────────────────
    //
    // A discard was once judged alone, so one restating a fact a live entry still carried was
    // reported as lost. Every judge text must say a surviving fact is not a loss.

    [TestMethod]
    [DataRow("question")]
    [DataRow("built-in directive")]
    [DataRow("memory-audit.md")]
    public void EveryJudgeTextStatesTheEphemeralSurvivalRule(string source)
    {
        StringAssert.Contains(
            Normalize(JudgeText(source, MemoryAuditEvaluator.EphemeralArchiveCategory)),
            Normalize(MemoryAuditEvaluator.EphemeralSurvivalRule),
            $"The {source} must say that a discarded fact a live entry still carries was not lost.");
    }

    // ── Reinforced-entry subject rule ────────────────────────────────────────
    //
    // Asked only whether an entry was a "vague blob", the judge passed and then failed the same
    // unchanged entries. Every judge text must give the same concrete definition of one subject.

    [TestMethod]
    [DataRow("question")]
    [DataRow("built-in directive")]
    [DataRow("memory-audit.md")]
    public void EveryJudgeTextStatesTheReinforcementSubjectRule(string source)
    {
        StringAssert.Contains(
            Normalize(JudgeText(source, MemoryAuditEvaluator.HighReinforcementCategory)),
            Normalize(MemoryAuditEvaluator.ReinforcementSubjectRule),
            $"The {source} must say what counts as one subject for a reinforced entry.");
    }

    private static string JudgeText(string source, string category = MemoryAuditEvaluator.NearDuplicateCategory) => source switch
    {
        "question" => MemoryAuditEvaluator.Question(category),
        "built-in directive" => MemoryAuditEvaluator.BuiltInDirective,
        "memory-audit.md" => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "memory-audit.md")),
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    private static string Normalize(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();

    // Splits on sentence ends and markdown bullets, so one bullet's wording cannot vouch for
    // another's. `sound=true` has no spaces, so the dot-space split never cuts through it.
    private static IEnumerable<string> Sentences(string text) =>
        System.Text.RegularExpressions.Regex.Split(Normalize(text), @"(?<=[.:;])\s|\s-\s|—");

    [TestMethod]
    public void SamplesMergesWithTheirSurvivingSources()
    {
        var source = Archived("src", "merged into m1", Now.AddDays(-2));
        var merged = Entry("m1") with
        {
            UpdatedAt = Now.AddDays(-2),
            Metadata = MergedFrom("src")
        };

        var samples = MemoryAuditEvaluator.SelectSamples([source, merged], [], Options(), Now);

        var merge = samples.Single(s => s.Category == MemoryAuditEvaluator.MergeCategory);
        CollectionAssert.AreEquivalent(new[] { "m1", "src" }, merge.Ids.ToArray());
        StringAssert.Contains(merge.Text, "Sources that were merged away");
    }

    [TestMethod]
    public void AMergeWhoseSourcesAreAllPurgedIsNotSampled()
    {
        // There is nothing to compare the replacement against, so there is no judgeable question.
        var merged = Entry("m1") with { UpdatedAt = Now.AddDays(-1), Metadata = MergedFrom("gone") };

        var samples = MemoryAuditEvaluator.SelectSamples([merged], [], Options(), Now);

        Assert.AreEqual(0, samples.Count(s => s.Category == MemoryAuditEvaluator.MergeCategory));
    }

    [TestMethod]
    public void MergesOlderThanTheEvalWindowAreNotSampled()
    {
        var source = Archived("src", "merged into m1", Now.AddDays(-90));
        var merged = Entry("m1") with { UpdatedAt = Now.AddDays(-90), Metadata = MergedFrom("src") };

        var samples = MemoryAuditEvaluator.SelectSamples([source, merged], [], Options(), Now);

        Assert.AreEqual(0, samples.Count(s => s.Category == MemoryAuditEvaluator.MergeCategory));
    }

    [TestMethod]
    public void SamplesNearDuplicatePairsHighReinforcementAndEphemeralArchives()
    {
        var a = Entry("a");
        var b = Entry("b");
        var heavy = Entry("heavy") with { ReinforcementCount = 40 };
        var dropped = Archived("dropped", DreamService.EphemeralArchiveReason, Now.AddDays(-1));

        var samples = MemoryAuditEvaluator.SelectSamples(
            [a, b, heavy, dropped],
            [new ShingleSimilarity.Pair("a", "b", 0.8)],
            Options(),
            Now);

        Assert.AreEqual(1, samples.Count(s => s.Category == MemoryAuditEvaluator.NearDuplicateCategory));
        Assert.AreEqual(1, samples.Count(s => s.Category == MemoryAuditEvaluator.HighReinforcementCategory));
        Assert.AreEqual(1, samples.Count(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory));
    }

    // ── Ephemeral discards in context ────────────────────────────────────────
    //
    // A discard is only a loss if nothing live still carries it, and the survivor is routinely
    // filed under a different category, so the judge is shown the closest live entries.

    [TestMethod]
    public void AnEphemeralSampleShowsTheLiveEntriesMostLikeIt()
    {
        var dropped = Archived("dropped", DreamService.EphemeralArchiveReason, Now.AddDays(-1)) with
        {
            Content = "Working hours are set to the Example Standard time zone.",
            Category = "episodic/decision"
        };
        var survivor = Entry("survivor") with
        {
            Content = "The agent schedules in the Example Standard time zone.",
            Category = "agent-knowledge/infrastructure"
        };
        var weaker = Entry("weaker") with { Content = "Another fact about time.", Category = "general" };

        var neighbours = new Dictionary<string, IReadOnlyList<MemorySimilarityMatch>>
        {
            ["dropped"] =
            [
                new(survivor, 0.83, MemorySimilarityMeasure.Embedding),
                new(weaker, 0.41, MemorySimilarityMeasure.Embedding)
            ]
        };

        var sample = MemoryAuditEvaluator.SelectSamples(
                [dropped, survivor, weaker], [], Options(), Now, liveNeighbours: neighbours)
            .Single(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory);

        CollectionAssert.AreEqual(new[] { "dropped" }, sample.Ids.ToArray(),
            "Live context is evidence, not part of the decision.");
        CollectionAssert.AreEqual(new[] { "survivor", "weaker" }, sample.ContextIds!.ToArray());
        StringAssert.Contains(sample.Text, "Most similar live entries (embedding similarity):");
        StringAssert.Contains(sample.Text,
            "[survivor] (0.83, category agent-knowledge/infrastructure) The agent schedules in the Example Standard time zone.");
        Assert.IsTrue(sample.Text.IndexOf("[survivor]", StringComparison.Ordinal)
                      < sample.Text.IndexOf("[weaker]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AnEphemeralSampleWithNoSimilarLiveEntriesSaysSo()
    {
        var dropped = Archived("dropped", DreamService.EphemeralArchiveReason, Now.AddDays(-1));

        var sample = MemoryAuditEvaluator.SelectSamples(
                [dropped], [], Options(), Now,
                liveNeighbours: new Dictionary<string, IReadOnlyList<MemorySimilarityMatch>> { ["dropped"] = [] })
            .Single(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory);

        StringAssert.Contains(sample.Text, "Most similar live entries: none found.");
        Assert.IsNull(sample.ContextIds);
    }

    [TestMethod]
    public void AnUnsearchedEphemeralSampleIsNotPresentedAsHavingNoSurvivor()
    {
        // "Not searched" and "searched, found nothing" must never read the same: only the second
        // is evidence that the fact is gone.
        var dropped = Archived("dropped", DreamService.EphemeralArchiveReason, Now.AddDays(-1));

        var unsearched = MemoryAuditEvaluator.SelectSamples([dropped], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory);
        var failed = MemoryAuditEvaluator.SelectSamples(
                [dropped], [], Options(), Now,
                liveNeighbours: new Dictionary<string, IReadOnlyList<MemorySimilarityMatch>>())
            .Single(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory);

        StringAssert.Contains(unsearched.Text, "Live memory was not searched");
        StringAssert.Contains(failed.Text, "search of live memory for entries like this one failed");
        Assert.IsFalse(unsearched.Text.Contains("none found", StringComparison.Ordinal));
        Assert.IsFalse(failed.Text.Contains("none found", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SelectEphemeralDiscardsPicksExactlyWhatIsSampled()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(i => Archived($"d{i}", DreamService.EphemeralArchiveReason, Now.AddDays(-i)))
            .Append(Archived("old", DreamService.EphemeralArchiveReason, Now.AddDays(-90)))
            .Append(Archived("merged", "merged into m1", Now.AddDays(-1)))
            .ToList();
        var options = new MemoryAuditOptions { EvalSampleSize = 3 };

        var discards = MemoryAuditEvaluator.SelectEphemeralDiscards(entries, options, Now);
        var sampled = MemoryAuditEvaluator.SelectSamples(entries, [], options, Now)
            .Where(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory)
            .Select(s => s.Ids[0]);

        CollectionAssert.AreEqual(new[] { "d0", "d1", "d2" }, discards.Select(e => e.Id).ToArray());
        CollectionAssert.AreEqual(discards.Select(e => e.Id).ToArray(), sampled.ToArray());
    }

    [TestMethod]
    public async Task ContextIdsAreCarriedOntoTheVerdict()
    {
        var samples = new List<MemoryAuditEvaluator.Sample>
        {
            new(MemoryAuditEvaluator.EphemeralArchiveCategory, ["dropped"], "text", ContextIds: ["survivor"])
        };

        var llm = new StubLlmClient("""{"verdicts":[{"index":1,"sound":true,"reason":"Survives."}]}""");

        var result = await new MemoryAuditEvaluator(llm, NullLogger.Instance)
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", null, CancellationToken.None);

        var verdict = result!.Verdicts.Single();
        CollectionAssert.AreEqual(new[] { "dropped" }, verdict.Ids.ToArray());
        CollectionAssert.AreEqual(new[] { "survivor" }, verdict.ContextIds!.ToArray());
    }

    // ── Near-duplicate sampling ──────────────────────────────────────────────
    //
    // Taking the top pairs by score re-judged the same pairs every week: identical text always
    // scored first and one cluster's n(n−1)/2 pairs filled the budget.

    [TestMethod]
    public void AClusterContributesOnePair()
    {
        string[] cluster = ["c1", "c2", "c3", "c4"];
        var pairs = new List<ShingleSimilarity.Pair>();
        for (var i = 0; i < cluster.Length; i++)
            for (var j = i + 1; j < cluster.Length; j++)
                pairs.Add(new ShingleSimilarity.Pair(
                    cluster[i], cluster[j], (cluster[i], cluster[j]) == ("c2", "c3") ? 0.95 : 0.7));
        pairs.Add(new ShingleSimilarity.Pair("x", "y", 0.6));

        var samples = NearDuplicates(
            [.. cluster.Select(Entry), Entry("x"), Entry("y")], pairs, Options());

        Assert.AreEqual(2, samples.Count);
        CollectionAssert.AreEqual(new[] { "c2", "c3" }, samples[0].Ids.ToArray());
        CollectionAssert.AreEqual(new[] { "x", "y" }, samples[1].Ids.ToArray());
    }

    [TestMethod]
    public void ClustersJudgedInThePreviousEvalAreSampledLast()
    {
        var samples = NearDuplicates(
            [Entry("a1"), Entry("a2"), Entry("b1"), Entry("b2"), Entry("c1"), Entry("c2")],
            [
                new ShingleSimilarity.Pair("a1", "a2", 0.9),
                new ShingleSimilarity.Pair("b1", "b2", 0.8),
                new ShingleSimilarity.Pair("c1", "c2", 0.7)
            ],
            new MemoryAuditOptions { EvalSampleSize = 2 },
            previouslyJudgedIds: new HashSet<string> { "a2" });

        CollectionAssert.AreEqual(
            new[] { "b1", "c1" },
            samples.Select(s => s.Ids[0]).ToArray());
    }

    [TestMethod]
    public void PreviouslyJudgedClustersStillFillLeftoverBudget()
    {
        var samples = NearDuplicates(
            [Entry("a1"), Entry("a2"), Entry("b1"), Entry("b2")],
            [
                new ShingleSimilarity.Pair("a1", "a2", 0.9),
                new ShingleSimilarity.Pair("b1", "b2", 0.8)
            ],
            Options(),
            previouslyJudgedIds: new HashSet<string> { "a1", "b1" });

        Assert.AreEqual(2, samples.Count);
    }

    [TestMethod]
    public void ExactCopiesTheDreamFoldsAreNotSampled()
    {
        var a = Entry("a") with { Content = "The same fact.", Category = "facts" };
        var b = Entry("b") with { Content = "The  same fact. ", Category = "facts" };

        var samples = NearDuplicates([a, b], [new ShingleSimilarity.Pair("a", "b", 1.0)], Options());

        Assert.AreEqual(0, samples.Count);
    }

    [TestMethod]
    public void IdenticalTextInDifferentCategoriesIsStillSampled()
    {
        // The fold requires a matching category, so these stay live and are still a question.
        var a = Entry("a") with { Content = "The same fact.", Category = "facts" };
        var b = Entry("b") with { Content = "The same fact.", Category = "preferences" };

        var samples = NearDuplicates([a, b], [new ShingleSimilarity.Pair("a", "b", 1.0)], Options());

        Assert.AreEqual(1, samples.Count);
    }

    [TestMethod]
    public void AnExactPairLinkedToADistinctEntryStillSamplesTheCluster()
    {
        var a = Entry("a") with { Content = "The same fact.", Category = "facts" };
        var b = Entry("b") with { Content = "The same fact.", Category = "facts" };
        var c = Entry("c") with { Content = "The same fact, restated.", Category = "facts" };

        var samples = NearDuplicates(
            [a, b, c],
            [
                new ShingleSimilarity.Pair("a", "b", 1.0),
                new ShingleSimilarity.Pair("a", "c", 0.8),
                new ShingleSimilarity.Pair("b", "c", 0.8)
            ],
            Options());

        Assert.AreEqual(1, samples.Count);
        CollectionAssert.Contains(samples[0].Ids.ToArray(), "c");
    }

    [TestMethod]
    public void NearDuplicateSelectionIsDeterministic()
    {
        var entries = new[] { "a1", "a2", "b1", "b2", "c1", "c2" }.Select(Entry).ToList();
        var pairs = new List<ShingleSimilarity.Pair>
        {
            new("a1", "a2", 0.8),
            new("b1", "b2", 0.8),
            new("c1", "c2", 0.8)
        };
        var options = new MemoryAuditOptions { EvalSampleSize = 2 };

        var forward = NearDuplicates(entries, pairs, options);
        var reversed = NearDuplicates([.. entries.AsEnumerable().Reverse()], [.. pairs.AsEnumerable().Reverse()], options);

        CollectionAssert.AreEqual(
            forward.SelectMany(s => s.Ids).ToArray(),
            reversed.SelectMany(s => s.Ids).ToArray());
        CollectionAssert.AreEqual(new[] { "a1", "a2", "b1", "b2" }, forward.SelectMany(s => s.Ids).ToArray());
    }

    private static List<MemoryAuditEvaluator.Sample> NearDuplicates(
        IReadOnlyList<MemoryEntry> entries,
        IReadOnlyList<ShingleSimilarity.Pair> pairs,
        MemoryAuditOptions options,
        IReadOnlySet<string>? previouslyJudgedIds = null) =>
        [.. MemoryAuditEvaluator.SelectSamples(entries, pairs, options, Now, previouslyJudgedIds: previouslyJudgedIds)
            .Where(s => s.Category == MemoryAuditEvaluator.NearDuplicateCategory)];

    [TestMethod]
    public void EachFamilyIsCappedAtTheSampleSize()
    {
        var entries = Enumerable.Range(0, 30)
            .Select(i => Entry($"e{i}") with { ReinforcementCount = 50 })
            .ToList();

        var samples = MemoryAuditEvaluator.SelectSamples(
            entries, [], new MemoryAuditOptions { EvalSampleSize = 4 }, Now);

        Assert.AreEqual(4, samples.Count(s => s.Category == MemoryAuditEvaluator.HighReinforcementCategory));
    }

    [TestMethod]
    public async Task VerdictsAreParsedAndAttachedToTheRightEntries()
    {
        var samples = new List<MemoryAuditEvaluator.Sample>
        {
            new(MemoryAuditEvaluator.MergeCategory, ["m1", "s1"], "first"),
            new(MemoryAuditEvaluator.MergeCategory, ["m2", "s2"], "second")
        };

        var llm = new StubLlmClient(
            """
            {"verdicts":[
              {"index":1,"sound":false,"reason":"Dropped the account number."},
              {"index":2,"sound":true,"reason":"Kept every specific."}
            ]}
            """);

        var result = await new MemoryAuditEvaluator(llm, NullLogger.Instance)
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Summary.Sampled);
        Assert.AreEqual(1, result.Summary.Sound);
        Assert.AreEqual(0.5, result.Summary.SoundRate, 1e-9);
        Assert.AreEqual("FP", result.StoreFingerprint);

        var unsound = result.Verdicts.Single(v => !v.Sound);
        CollectionAssert.AreEquivalent(new[] { "m1", "s1" }, unsound.Ids.ToArray());
        Assert.AreEqual("Dropped the account number.", unsound.Reason);
    }

    [TestMethod]
    public async Task AnOutOfRangeIndexIsDroppedRatherThanMisattributed()
    {
        var samples = new List<MemoryAuditEvaluator.Sample>
        {
            new(MemoryAuditEvaluator.MergeCategory, ["m1"], "only one")
        };

        var llm = new StubLlmClient(
            """{"verdicts":[{"index":1,"sound":true},{"index":7,"sound":false,"reason":"nonsense"}]}""");

        var result = await new MemoryAuditEvaluator(llm, NullLogger.Instance)
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", null, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Verdicts.Count);
    }

    [TestMethod]
    public async Task AnUnparseableReplyYieldsNoResultRatherThanAPerfectScore()
    {
        var samples = new List<MemoryAuditEvaluator.Sample>
        {
            new(MemoryAuditEvaluator.MergeCategory, ["m1"], "only one")
        };

        var llm = new StubLlmClient("the model rambled and produced no JSON");

        var result = await new MemoryAuditEvaluator(llm, NullLogger.Instance)
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", null, CancellationToken.None);

        Assert.IsNull(result);
    }

    // ── Content visibility ───────────────────────────────────────────────────
    //
    // Entries were once cut at 600 characters. Merged replacements are the longest entries in
    // the store, so the judge flagged a merge as having dropped details that sat past the cut —
    // and could equally have passed one that really had.

    [TestMethod]
    public void ALongMergeReplacementIsShownWhole()
    {
        var source = Archived("src", "merged into m1", Now.AddDays(-2)) with
        {
            Content = "Routing note about tokensurprise inflation."
        };
        var merged = Entry("m1") with
        {
            Content = Padding(2_700) + " the tokensurprise guidance survives here",
            UpdatedAt = Now.AddDays(-2),
            Metadata = MergedFrom("src")
        };

        var sample = MemoryAuditEvaluator.SelectSamples([source, merged], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.MergeCategory);

        StringAssert.Contains(sample.Text, "the tokensurprise guidance survives here");
        Assert.IsFalse(sample.Truncated);
        Assert.IsFalse(sample.Text.Contains("[truncated", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LongReinforcedAndNearDuplicateEntriesAreShownWhole()
    {
        var heavy = Entry("heavy") with { Content = Padding(4_000) + " heavy-tail", ReinforcementCount = 40 };
        var a = Entry("a") with { Content = Padding(4_000) + " a-tail" };
        var b = Entry("b") with { Content = Padding(4_000) + " b-tail" };

        var samples = MemoryAuditEvaluator.SelectSamples(
            [heavy, a, b], [new ShingleSimilarity.Pair("a", "b", 0.9)], Options(), Now);

        var reinforced = samples.Single(s => s.Category == MemoryAuditEvaluator.HighReinforcementCategory);
        StringAssert.Contains(reinforced.Text, "heavy-tail");
        Assert.IsFalse(reinforced.Truncated);

        var pair = samples.Single(s => s.Category == MemoryAuditEvaluator.NearDuplicateCategory);
        StringAssert.Contains(pair.Text, "a-tail");
        StringAssert.Contains(pair.Text, "b-tail");
        Assert.IsFalse(pair.Truncated);
    }

    [TestMethod]
    public void ContentOverTheCapIsMarkedWithHowMuchWasShown()
    {
        var total = MemoryAuditEvaluator.MaxContentChars + 1_000;
        var heavy = Entry("heavy") with { Content = Padding(total), ReinforcementCount = 40 };

        var sample = MemoryAuditEvaluator.SelectSamples([heavy], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.HighReinforcementCategory);

        Assert.IsTrue(sample.Truncated);
        StringAssert.Contains(sample.Text,
            $"[truncated: showed {MemoryAuditEvaluator.MaxContentChars} of {total} chars]");
    }

    [TestMethod]
    public async Task TheJudgeIsToldAboutTruncationOnlyWhenSomethingWasCut()
    {
        var samples = new List<MemoryAuditEvaluator.Sample>
        {
            new(MemoryAuditEvaluator.HighReinforcementCategory, ["heavy"], "cut", Truncated: true),
            new(MemoryAuditEvaluator.EphemeralArchiveCategory, ["dropped"], "whole")
        };

        var llm = new StubLlmClient("""{"verdicts":[{"index":1,"sound":true}]}""");

        await new MemoryAuditEvaluator(llm, NullLogger.Instance)
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", null, CancellationToken.None);

        var prompts = llm.UserMessages;
        Assert.AreEqual(2, prompts.Count);
        StringAssert.Contains(prompts.Single(p => p.Contains(MemoryAuditEvaluator.HighReinforcementCategory)),
            MemoryAuditEvaluator.TruncationNotice);
        Assert.IsFalse(prompts.Single(p => p.Contains(MemoryAuditEvaluator.EphemeralArchiveCategory))
            .Contains(MemoryAuditEvaluator.TruncationNotice, StringComparison.Ordinal));
    }

    [TestMethod]
    public void AMergeSampleReportsSpecificsMissingFromTheReplacement()
    {
        var source = Archived("src", "merged into m1", Now.AddDays(-2)) with
        {
            Content = "The invoice from Contoso was paid on 2026-03-14."
        };
        var merged = Entry("m1") with
        {
            Content = "An invoice was paid on 2026-03-14.",
            UpdatedAt = Now.AddDays(-2),
            Metadata = MergedFrom("src")
        };

        var sample = MemoryAuditEvaluator.SelectSamples([source, merged], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.MergeCategory);

        var coverage = CoverageLineOf(sample);
        StringAssert.Contains(coverage, "do not appear verbatim");
        StringAssert.Contains(coverage, "Contoso");
        Assert.IsFalse(coverage.Contains("2026", StringComparison.Ordinal),
            "A date the replacement kept must not be reported as missing.");
    }

    [TestMethod]
    public void AMergeSampleSaysSoWhenNothingIsMissing()
    {
        var source = Archived("src", "merged into m1", Now.AddDays(-2)) with
        {
            Content = "The invoice from Contoso was paid on 2026-03-14."
        };
        var merged = Entry("m1") with
        {
            Content = "Contoso's invoice was paid on 2026-03-14.",
            UpdatedAt = Now.AddDays(-2),
            Metadata = MergedFrom("src")
        };

        var sample = MemoryAuditEvaluator.SelectSamples([source, merged], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.MergeCategory);

        Assert.AreEqual(MemoryAuditEvaluator.CoverageLine([]), CoverageLineOf(sample));
    }

    [TestMethod]
    public void TheCoverageCheckSeesPastTheCap()
    {
        // The rendered replacement is cut, but the check runs over the full content — so a
        // specific kept only past the cut is still credited.
        var source = Archived("src", "merged into m1", Now.AddDays(-2)) with
        {
            Content = "The invoice from Contoso was paid."
        };
        var merged = Entry("m1") with
        {
            Content = Padding(MemoryAuditEvaluator.MaxContentChars + 500) + " Contoso",
            UpdatedAt = Now.AddDays(-2),
            Metadata = MergedFrom("src")
        };

        var sample = MemoryAuditEvaluator.SelectSamples([source, merged], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.MergeCategory);

        Assert.IsTrue(sample.Truncated);
        Assert.AreEqual(MemoryAuditEvaluator.CoverageLine([]), CoverageLineOf(sample));
    }

    private static string CoverageLineOf(MemoryAuditEvaluator.Sample sample) =>
        sample.Text.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith("Coverage check:", StringComparison.Ordinal));

    // Lowercase, so it contributes no specifics to the coverage check, and no whitespace, so
    // the renderer's trim cannot change its length.
    private static string Padding(int length) => new('x', length);

    [TestMethod]
    public void TheFingerprintTracksLiveIdsAndTheArchiveSize()
    {
        var a = Entry("a");
        var b = Entry("b");

        var baseline = MemoryAuditEvaluator.StoreFingerprint([a, b]);

        Assert.AreEqual(baseline, MemoryAuditEvaluator.StoreFingerprint([b, a]),
            "Enumeration order must not change the fingerprint.");
        Assert.AreNotEqual(baseline, MemoryAuditEvaluator.StoreFingerprint([a, b, Entry("c")]));
        Assert.AreNotEqual(baseline, MemoryAuditEvaluator.StoreFingerprint(
            [a, b, Archived("z", "ephemeral", Now)]));
    }

    // ── Carrying verdicts forward ────────────────────────────────────────────
    //
    // The judge reached opposite verdicts on identical input, so a family's trend moved when no
    // entry had. A sample whose question, directive and content are unchanged keeps its verdict.

    [TestMethod]
    public void AReinforcedEntrysEvidenceKeyIgnoresItsCountsButNotItsContent()
    {
        var heavy = Entry("heavy") with { ReinforcementCount = 40, ImportanceScore = 0.5f };

        var baseline = ReinforcedSample(heavy);
        var reinforcedAgain = ReinforcedSample(heavy with { ReinforcementCount = 41, ImportanceScore = 0.9f });
        var edited = ReinforcedSample(heavy with { Content = "a different fact" });

        Assert.AreNotEqual(baseline.Text, reinforcedAgain.Text, "The judge still sees the new count.");
        Assert.AreEqual(baseline.EvidenceKey, reinforcedAgain.EvidenceKey);
        Assert.AreNotEqual(baseline.EvidenceKey, edited.EvidenceKey);
    }

    [TestMethod]
    public void AnEphemeralEvidenceKeyTracksItsLiveNeighboursButNotTheirScores()
    {
        var dropped = Archived("dropped", DreamService.EphemeralArchiveReason, Now.AddDays(-1));
        var survivor = Entry("survivor");

        string? KeyWith(MemoryEntry neighbour, double score) =>
            MemoryAuditEvaluator.SelectSamples(
                    [dropped, neighbour], [], Options(), Now,
                    liveNeighbours: new Dictionary<string, IReadOnlyList<MemorySimilarityMatch>>
                    {
                        ["dropped"] = [new(neighbour, score, MemorySimilarityMeasure.Lexical)]
                    })
                .Single(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory)
                .EvidenceKey;

        var baseline = KeyWith(survivor, 0.61);

        Assert.AreEqual(baseline, KeyWith(survivor, 0.74));
        Assert.AreNotEqual(baseline, KeyWith(survivor with { Content = "a changed survivor" }, 0.61));
        Assert.AreNotEqual(baseline, MemoryAuditEvaluator.SelectSamples([dropped], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.EphemeralArchiveCategory)
            .EvidenceKey, "An unsearched discard is not the same evidence as one with a neighbour.");
    }

    [TestMethod]
    public void AMergeEvidenceKeyTracksItsSources()
    {
        var source = Archived("src", "merged into m1", Now.AddDays(-2));
        var merged = Entry("m1") with { UpdatedAt = Now.AddDays(-2), Metadata = MergedFrom("src") };

        string? KeyOf(MemoryEntry s) =>
            MemoryAuditEvaluator.SelectSamples([s, merged], [], Options(), Now)
                .Single(x => x.Category == MemoryAuditEvaluator.MergeCategory)
                .EvidenceKey;

        Assert.AreEqual(KeyOf(source), KeyOf(source));
        Assert.AreNotEqual(KeyOf(source), KeyOf(source with { Content = "a source that said more" }));
    }

    [TestMethod]
    public async Task AnUnchangedSampleCarriesItsVerdictWithoutAskingTheJudge()
    {
        List<MemoryAuditEvaluator.Sample> samples = [ReinforcedSample(Entry("heavy") with { ReinforcementCount = 40 })];

        var first = await new MemoryAuditEvaluator(
                new StubLlmClient("""{"verdicts":[{"index":1,"sound":true,"reason":"One subject."}]}"""),
                NullLogger.Instance)
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP1", null, CancellationToken.None);

        var laterSamples = new List<MemoryAuditEvaluator.Sample>
        {
            ReinforcedSample(Entry("heavy") with { ReinforcementCount = 55 })
        };
        var judge = new StubLlmClient("""{"verdicts":[{"index":1,"sound":false,"reason":"Flipped.","evidence":"a fact"}]}""");

        var second = await new MemoryAuditEvaluator(judge, NullLogger.Instance)
            .EvaluateAsync(laterSamples, "directive", ModelTier.Balanced, "FP2", first!.Verdicts, CancellationToken.None);

        Assert.AreEqual(0, judge.UserMessages.Count, "An unchanged sample must not be judged again.");
        var verdict = second!.Verdicts.Single();
        Assert.IsTrue(verdict.Sound);
        Assert.IsTrue(verdict.Carried);
        Assert.AreEqual("One subject.", verdict.Reason);
        Assert.AreEqual(first.Verdicts.Single().JudgedAt, verdict.JudgedAt);
        Assert.AreEqual(1, second.Summary.Carried);
        Assert.IsFalse(first.Verdicts.Single().Carried);
        Assert.AreEqual(0, first.Summary.Carried);
    }

    [TestMethod]
    public async Task AChangedDirectiveIsJudgedAfresh()
    {
        List<MemoryAuditEvaluator.Sample> samples = [ReinforcedSample(Entry("heavy") with { ReinforcementCount = 40 })];
        var previous = new[] { CarriableVerdict(samples[0], "old directive") };
        var judge = new StubLlmClient("""{"verdicts":[{"index":1,"sound":true}]}""");

        var result = await new MemoryAuditEvaluator(judge, NullLogger.Instance)
            .EvaluateAsync(samples, "new directive", ModelTier.Balanced, "FP", previous, CancellationToken.None);

        Assert.AreEqual(1, judge.UserMessages.Count);
        Assert.IsFalse(result!.Verdicts.Single().Carried);
    }

    [TestMethod]
    public async Task OnlyChangedSamplesAreSentToTheJudge()
    {
        var unchanged = ReinforcedSample(Entry("same") with { Content = "unchanged-entry-text", ReinforcementCount = 50 });
        var changed = ReinforcedSample(Entry("new") with { Content = "changed-entry-text", ReinforcementCount = 40 });
        var judge = new StubLlmClient("""{"verdicts":[{"index":1,"sound":true,"reason":"Fresh."}]}""");

        var result = await new MemoryAuditEvaluator(judge, NullLogger.Instance)
            .EvaluateAsync(
                [unchanged, changed], "directive", ModelTier.Balanced, "FP",
                [CarriableVerdict(unchanged, "directive")], CancellationToken.None);

        var prompt = judge.UserMessages.Single();
        StringAssert.Contains(prompt, "changed-entry-text");
        Assert.IsFalse(prompt.Contains("unchanged-entry-text", StringComparison.Ordinal));
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(prompt, @"^1\.\r?$", System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(prompt, @"^2\.\r?$", System.Text.RegularExpressions.RegexOptions.Multiline),
            "The one item sent is numbered 1.");

        var fresh = result!.Verdicts.Single(v => !v.Carried);
        CollectionAssert.AreEqual(new[] { "new" }, fresh.Ids.ToArray());
        Assert.AreEqual("Fresh.", fresh.Reason);
        CollectionAssert.AreEqual(new[] { "same" }, result.Verdicts.Single(v => v.Carried).Ids.ToArray());
    }

    [TestMethod]
    public async Task CarriedVerdictsSurviveAFailedCallForTheirFamily()
    {
        var unchanged = ReinforcedSample(Entry("same") with { ReinforcementCount = 50 });
        var changed = ReinforcedSample(Entry("new") with { ReinforcementCount = 40 });
        var judge = new StubLlmClient("the model rambled and produced no JSON");

        var result = await new MemoryAuditEvaluator(judge, NullLogger.Instance)
            .EvaluateAsync(
                [unchanged, changed], "directive", ModelTier.Balanced, "FP",
                [CarriableVerdict(unchanged, "directive")], CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "same" }, result!.Verdicts.Single().Ids.ToArray());
    }

    // ── Quoted evidence ──────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("it bundles several topics", false)]
    [DataRow("server alpha hosts the files", true)]
    [DataRow("  \"SERVER ALPHA\n  hosts the files\"  ", true)]
    public async Task AnUnsoundReinforcedVerdictCountsOnlyWhenItQuotesTheEntry(string? evidence, bool counted)
    {
        var sample = ReinforcedSample(Entry("heavy") with
        {
            Content = "Server alpha hosts the files. The weather is mild.",
            ReinforcementCount = 40
        });
        var reply = JsonSerializer.Serialize(new
        {
            verdicts = new[] { new { index = 1, sound = false, reason = "Two subjects.", evidence } }
        });

        var result = await new MemoryAuditEvaluator(new StubLlmClient(reply), NullLogger.Instance)
            .EvaluateAsync([sample], "directive", ModelTier.Balanced, "FP", null, CancellationToken.None);

        if (counted)
            Assert.AreEqual(evidence!.Trim(), result!.Verdicts.Single().Evidence);
        else
            Assert.IsNull(result, "An unquoted finding must not be scored.");
    }

    [TestMethod]
    public async Task AnUnsoundMergeVerdictIsKeptWithoutAQuote()
    {
        var samples = new List<MemoryAuditEvaluator.Sample>
        {
            new(MemoryAuditEvaluator.MergeCategory, ["m1", "s1"], "merge text")
        };
        var llm = new StubLlmClient("""{"verdicts":[{"index":1,"sound":false,"reason":"Dropped a date."}]}""");

        var result = await new MemoryAuditEvaluator(llm, NullLogger.Instance)
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", null, CancellationToken.None);

        var verdict = result!.Verdicts.Single();
        Assert.IsFalse(verdict.Sound);
        Assert.IsNull(verdict.Evidence);
    }

    [TestMethod]
    public void EveryPromptAsksForQuotedEvidence()
    {
        StringAssert.Contains(MemoryAuditEvaluator.AnswerFormat, "\"evidence\"");
        StringAssert.Contains(MemoryAuditEvaluator.BuiltInDirective, "\"evidence\"");
        StringAssert.Contains(JudgeText("memory-audit.md"), "\"evidence\"");
    }

    private static MemoryAuditEvaluator.Sample ReinforcedSample(MemoryEntry entry) =>
        MemoryAuditEvaluator.SelectSamples([entry], [], Options(), Now)
            .Single(s => s.Category == MemoryAuditEvaluator.HighReinforcementCategory);

    private static MemoryAuditEvalVerdict CarriableVerdict(MemoryAuditEvaluator.Sample sample, string directive) =>
        new(sample.Category, sample.Ids, true, "Judged before.",
            Key: MemoryAuditEvaluator.JudgeKey(sample, directive),
            JudgedAt: Now.AddDays(-7));

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MemoryAuditOptions Options() => new();

    private static MemoryEntry Entry(string id) =>
        new(id, $"a fact about {id}", null, [], Now.AddDays(-30));

    private static MemoryEntry Archived(string id, string reason, DateTimeOffset at) =>
        Entry(id) with { ArchivedAt = at, ArchiveReason = reason };

    private static Dictionary<string, string> MergedFrom(params string[] ids) => new()
    {
        [DreamService.MergedFromKey] = string.Join(",", ids),
        [DreamService.MergedAtKey] = Now.AddDays(-2).ToString("O")
    };

    private sealed class StubLlmClient(string response) : ILlmClient
    {
        /// <summary>The user message of every call, in order.</summary>
        public List<string> UserMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        {
            UserMessages.AddRange(messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options,
            CancellationToken cancellationToken) =>
            GetResponseAsync(messages, options, cancellationToken);
    }
}
