using Microsoft.Extensions.Logging;

namespace RockBot.Tools.FileSystem.Tests;

/// <summary>
/// Issue #677: a renamed copy written soon after an overwrite refusal is logged, and nothing
/// else changes.
/// </summary>
[TestClass]
public class SiblingCopyTests
{
    private const string LogPrefix = "Possible sibling copy after overwrite refusal";

    [TestMethod]
    [DataRow("drafts/deck-revised.md", "drafts/deck.md")]
    [DataRow("drafts/deck_v2.md", "drafts/deck.md")]
    [DataRow("drafts/deck-new.md", "drafts/deck.md")]
    [DataRow("drafts/deck-copy.md", "drafts/deck.md")]
    [DataRow("drafts/deck-UPDATED.md", "drafts/deck.md")]
    [DataRow("drafts/deck2.md", "drafts/deck.md")]
    [DataRow("exports/deck-revised.md", "drafts/deck.md")]
    public void IsLikely_CopySuffix_IsDetected(string newPath, string original) =>
        Assert.IsTrue(SiblingCopy.IsLikely(newPath, original));

    [TestMethod]
    [DataRow("drafts/deck.md", "drafts/deck.md")]
    [DataRow("drafts/deck-revised.txt", "drafts/deck.md")]
    [DataRow("drafts/deck-notes.md", "drafts/deck.md")]
    [DataRow("drafts/other-revised.md", "drafts/deck.md")]
    public void IsLikely_UnrelatedName_IsNotDetected(string newPath, string original) =>
        Assert.IsFalse(SiblingCopy.IsLikely(newPath, original));

    [TestMethod]
    public async Task RenamedCopyAfterRefusal_IsLoggedAndAllowed()
    {
        using var fx = new FileToolsFixture();
        fx.WriteRaw("drafts/deck.md", "original\n");
        Assert.IsTrue((await fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n")).IsError);

        var response = await fx.WriteAsync("s1", "drafts/deck-revised.md", "rewrite\n");

        Assert.IsFalse(response.IsError, "Detection is diagnostic only; the write goes ahead.");
        CollectionAssert.Contains(fx.Log.At(LogLevel.Information).ToList(),
            $"{LogPrefix}: drafts/deck-revised.md (refused: drafts/deck.md)");
    }

    [TestMethod]
    public async Task RenamedCopyWithoutRefusal_IsNotLogged()
    {
        using var fx = new FileToolsFixture();
        fx.WriteRaw("drafts/deck.md", "original\n");

        await fx.WriteAsync("s1", "drafts/deck-revised.md", "rewrite\n");

        Assert.IsFalse(fx.Log.Entries.Any(e => e.Message.StartsWith(LogPrefix)));
    }

    [TestMethod]
    public async Task RenamedCopyByAnotherSession_IsNotLogged()
    {
        using var fx = new FileToolsFixture();
        fx.WriteRaw("drafts/deck.md", "original\n");
        await fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        await fx.WriteAsync("s2", "drafts/deck-revised.md", "rewrite\n");

        Assert.IsFalse(fx.Log.Entries.Any(e => e.Message.StartsWith(LogPrefix)));
    }

    [TestMethod]
    public async Task RenamedCopyAfterTheWindow_IsNotLogged()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        using var fx = new FileToolsFixture(time: clock);
        fx.WriteRaw("drafts/deck.md", "original\n");
        await fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        clock.Advance(TimeSpan.FromMinutes(11));
        await fx.WriteAsync("s1", "drafts/deck-revised.md", "rewrite\n");

        Assert.IsFalse(fx.Log.Entries.Any(e => e.Message.StartsWith(LogPrefix)));
    }

    [TestMethod]
    public async Task OverwritingAnExistingSibling_IsNotLogged()
    {
        // Only a newly created file is a candidate copy.
        using var fx = new FileToolsFixture();
        fx.WriteRaw("drafts/deck.md", "original\n");
        Assert.IsFalse((await fx.WriteAsync("s1", "drafts/deck-revised.md", "v1\n")).IsError);
        await fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        await fx.WriteAsync("s1", "drafts/deck-revised.md", "v2\n");

        Assert.IsFalse(fx.Log.Entries.Any(e => e.Message.StartsWith(LogPrefix)));
    }

    [TestMethod]
    public void Refusals_AreBoundedPerSession()
    {
        var ledger = new FileReadLedger();
        for (var i = 0; i < 40; i++)
            ledger.RecordRefusal("s1", $"/vol/f{i}");

        var recent = ledger.RecentRefusals("s1");

        Assert.IsTrue(recent.Count <= 16, $"{recent.Count} refusals kept.");
        Assert.AreEqual("/vol/f39", recent[0], "Most recent first.");
    }
}
