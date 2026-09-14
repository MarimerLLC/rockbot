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

    private static string JudgeText(string source) => source switch
    {
        "question" => MemoryAuditEvaluator.Question(MemoryAuditEvaluator.NearDuplicateCategory),
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
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", CancellationToken.None);

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
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", CancellationToken.None);

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
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", CancellationToken.None);

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
            .EvaluateAsync(samples, "directive", ModelTier.Balanced, "FP", CancellationToken.None);

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
