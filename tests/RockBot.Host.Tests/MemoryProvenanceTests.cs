namespace RockBot.Host.Tests;

/// <summary>
/// Issue #667: memory provenance. A fact built from search snippets must stay marked as
/// inferred through reinforcement, extension and dream merges, and only new evidence from the
/// user or a tool may raise its trust.
/// </summary>
[TestClass]
public class MemoryProvenanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow("user", "user")]
    [DataRow(" User ", "user")]
    [DataRow("inferred", "inferred")]
    [DataRow("inference", "inferred")]
    [DataRow("tool:web_browse", "tool:web_browse")]
    [DataRow("tool: calendar", "tool:calendar")]
    [DataRow("tool:", null)]
    [DataRow("tool", null)]
    [DataRow("search snippets", null)]
    [DataRow("", null)]
    public void Normalize_AcceptsTheThreeShapesOnly(string input, string? expected)
    {
        Assert.AreEqual(expected, MemoryProvenance.Normalize(input));
    }

    [TestMethod]
    public void LeastTrusted_AnyInferredPartMakesTheWholeInferred()
    {
        Assert.AreEqual("inferred", MemoryProvenance.LeastTrusted(["user", null, "inferred"]));
    }

    [TestMethod]
    public void LeastTrusted_UnknownPartMakesTheSourceUnknown()
    {
        Assert.IsNull(MemoryProvenance.LeastTrusted(["user", null]));
    }

    [TestMethod]
    public void LeastTrusted_ToolBeatsNothingButLosesToUserNever()
    {
        Assert.AreEqual("tool:web_browse", MemoryProvenance.LeastTrusted(["user", "tool:web_browse"]));
    }

    [TestMethod]
    public void MostTrusted_UserConfirmationRaisesAnInferredFact()
    {
        Assert.AreEqual("user", MemoryProvenance.MostTrusted("inferred", "user"));
        Assert.AreEqual("user", MemoryProvenance.MostTrusted("user", "inferred"));
    }

    [TestMethod]
    public void Reinforce_UserRestatingAnInferredFact_RaisesItsSource()
    {
        var existing = Entry("a", "inferred");
        var candidate = Entry("b", "user");

        var reinforced = MemoryDeduplicator.Reinforce(existing, candidate, Now);

        Assert.AreEqual("user", MemoryProvenance.Get(reinforced.Metadata));
    }

    [TestMethod]
    public void Reinforce_InferredRestatementOfAUserFact_KeepsUser()
    {
        var reinforced = MemoryDeduplicator.Reinforce(Entry("a", "user"), Entry("b", "inferred"), Now);

        Assert.AreEqual("user", MemoryProvenance.Get(reinforced.Metadata));
    }

    [TestMethod]
    public void FoldMergeIntoExisting_TakesTheLeastTrustedSource()
    {
        var existing = Entry("a", "user");
        var merged = Entry("m", "inferred");

        var folded = DreamService.FoldMergeIntoExisting(existing, merged, [Entry("s1", "inferred")], Now);

        Assert.AreEqual("inferred", MemoryProvenance.Get(folded.Metadata));
    }

    [TestMethod]
    public void FoldExactDuplicates_TakesTheMostTrustedSource()
    {
        var folded = DreamService.FoldExactDuplicates(Entry("a", "inferred"), [Entry("b", "tool:calendar")], Now);

        Assert.AreEqual("tool:calendar", MemoryProvenance.Get(folded.Metadata));
    }

    [TestMethod]
    public void CountSources_BucketsToolsTogetherAndCountsMissing()
    {
        var counts = MemoryAuditAnalyzer.CountSources(
            [Entry("a", "user"), Entry("b", "tool:x"), Entry("c", "tool:y"), Entry("d", null), Entry("e", "inferred")]);

        Assert.AreEqual(1, counts["user"]);
        Assert.AreEqual(2, counts["tool"]);
        Assert.AreEqual(1, counts["none"]);
        Assert.AreEqual(1, counts["inferred"]);
    }

    private static MemoryEntry Entry(string id, string? source) =>
        new(id, $"fact {id}", "test", [], Now.AddDays(-1), Metadata: MemoryProvenance.With(null, source));
}
