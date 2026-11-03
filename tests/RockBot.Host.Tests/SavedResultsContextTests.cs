namespace RockBot.Host.Tests;

/// <summary>#418: a request for earlier work is shown the matching saved result.</summary>
[TestClass]
public class SavedResultsContextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);

    private static SavedResponse Saved(string id, string label, string content, int minutesAgo = 0) =>
        new(id, label, content, "RockBot", "s", T0.AddMinutes(-minutesAgo), SavedResponseSources.Subagent, true);

    private static readonly SavedResponse[] Store =
    [
        Saved("tcp", "Spawn a subagent to research this", "BBR vs CUBIC: CUBIC is the safe default. BBR paces near the bottleneck bandwidth; congestion control differs."),
        Saved("vendors", "Compare the three vendors", "Acme, Globex and Initech pricing and support terms compared."),
        Saved("brief", "morning-briefing", "Calendar: two meetings. Mail: nothing urgent."),
    ];

    [TestMethod]
    public void RequestForEarlierWork_MatchesItsResult_EvenWhenTheLabelDoesNot()
    {
        var matches = SavedResultsContext.SelectMatches(Store,
            "You did a BBR vs CUBIC congestion-control comparison for me today but I lost it. Can you get it back?");

        Assert.AreEqual("tcp", matches.Single().Id);
    }

    [TestMethod]
    public void EntriesSharingOnlyCommonWords_AreDropped_WhenARealMatchExists()
    {
        // Live false positives: long, unrelated saves whose bodies mention "long", "term", "memory".
        var filler = new string('x', 900) + " ";
        var store = Store.Concat(
        [
            Saved("old-1", "Person lookup", "Career summary. " + filler + "Long notes; memory of the test class, term dates.", minutesAgo: 9000),
            Saved("old-2", "Cyber vs solar punk", "Two genres. " + filler + "On memory, control and the long term future of test fiction.", minutesAgo: 9500),
        ]).ToList();

        var matches = SavedResultsContext.SelectMatches(store,
            "This is a software test; please don't save it to long-term memory. You did a BBR vs CUBIC " +
            "congestion-control comparison for me today but I lost it. Can you get it back for me?");

        CollectionAssert.AreEqual(new[] { "tcp" }, matches.Select(m => m.Id).ToArray());
    }

    [TestMethod]
    public void ConversationalFiller_DoesNotMatch()
    {
        Assert.AreEqual(0, SavedResultsContext.SelectMatches(Store,
            "Can you get back to me about that thing you did earlier today?").Count);
    }

    [TestMethod]
    public void OneSharedWord_IsNotEnough()
    {
        Assert.AreEqual(0, SavedResultsContext.SelectMatches(Store, "What does Acme do?").Count,
            "'acme' alone could be anything about Acme, not a request for the vendor comparison");
        Assert.AreEqual("vendors", SavedResultsContext.SelectMatches(Store, "Acme and Globex pricing again").Single().Id);
    }

    [TestMethod]
    public void BestMatchFirst_TiesGoToNewer_CappedAtMax()
    {
        var many = Enumerable.Range(0, 5)
            .Select(i => Saved($"brief-{i}", "morning-briefing", "Calendar meetings and urgent mail", minutesAgo: i * 60))
            .ToList();

        var matches = SavedResultsContext.SelectMatches(many, "the morning briefing calendar and mail");

        CollectionAssert.AreEqual(new[] { "brief-0", "brief-1", "brief-2" }, matches.Select(m => m.Id).ToArray());
    }

    [TestMethod]
    public void Render_ListsIdsAndSaysHowToUseThem_NullWhenNoMatches()
    {
        Assert.IsNull(SavedResultsContext.Render([]));

        var note = SavedResultsContext.Render([Store[0]]);
        StringAssert.Contains(note, "id=tcp");
        StringAssert.Contains(note, "get_saved_response");
        StringAssert.Contains(note, "instead of redoing");
    }
}
