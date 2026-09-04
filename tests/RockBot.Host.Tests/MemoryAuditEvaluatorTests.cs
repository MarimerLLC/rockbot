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
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ModelTier tier, ChatOptions? options,
            CancellationToken cancellationToken) =>
            GetResponseAsync(messages, options, cancellationToken);
    }
}
