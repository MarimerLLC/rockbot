namespace RockBot.Tools.FileSystem.Tests;

[TestClass]
public class FileReadLedgerTests
{
    private const string FilePath = "/vol/drafts/x.md";

    [TestMethod]
    public void UnknownFile_IsNeverRead()
    {
        var ledger = new FileReadLedger();
        Assert.AreEqual(FileCoverageStatus.NeverRead, ledger.Check("s", FilePath, "h1").Status);
    }

    [TestMethod]
    public void OverlappingAndAdjacentRanges_MergeToFull()
    {
        var ledger = new FileReadLedger();
        ledger.RecordRead("s", FilePath, "h1", 10, 1, 4);
        ledger.RecordRead("s", FilePath, "h1", 10, 8, 10);
        Assert.AreEqual(FileCoverageStatus.Partial, ledger.Check("s", FilePath, "h1").Status);
        Assert.AreEqual(5, ledger.Check("s", FilePath, "h1").FirstUnseenLine);
        Assert.AreEqual("lines 1–4 and 8–10", ledger.Check("s", FilePath, "h1").DescribeSeen());

        ledger.RecordRead("s", FilePath, "h1", 10, 3, 7);
        Assert.AreEqual(FileCoverageStatus.Full, ledger.Check("s", FilePath, "h1").Status);
    }

    [TestMethod]
    public void ReadOfNewVersion_DiscardsOldCoverage()
    {
        var ledger = new FileReadLedger();
        ledger.RecordFullyKnown("s", FilePath, "h1", 10);
        ledger.RecordRead("s", FilePath, "h2", 12, 1, 3);

        Assert.AreEqual(FileCoverageStatus.Stale, ledger.Check("s", FilePath, "h1").Status);
        Assert.AreEqual(FileCoverageStatus.Partial, ledger.Check("s", FilePath, "h2").Status);
    }

    [TestMethod]
    public void Sessions_AreIndependent()
    {
        var ledger = new FileReadLedger();
        ledger.RecordFullyKnown("primary", FilePath, "h1", 10);
        Assert.AreEqual(FileCoverageStatus.NeverRead, ledger.Check("subagent/1", FilePath, "h1").Status);
    }

    [TestMethod]
    public void EmptyFile_RecordedIsFull()
    {
        var ledger = new FileReadLedger();
        ledger.RecordFullyKnown("s", FilePath, "h0", 0);
        Assert.AreEqual(FileCoverageStatus.Full, ledger.Check("s", FilePath, "h0").Status);
    }

    [TestMethod]
    public void Overflow_EvictsLeastRecentlyTouched()
    {
        var ledger = new FileReadLedger(maxEntries: 4);
        for (var i = 0; i < 5; i++)
            ledger.RecordFullyKnown("s", $"/vol/f{i}", "h", 1);

        Assert.IsTrue(ledger.Count <= 4);
        Assert.AreEqual(FileCoverageStatus.NeverRead, ledger.Check("s", "/vol/f0", "h").Status,
            "The oldest entry goes first; losing it can only cause a safe refusal.");
        Assert.AreEqual(FileCoverageStatus.Full, ledger.Check("s", "/vol/f4", "h").Status);
    }

    [TestMethod]
    public void LineCount_IgnoresTrailingNewline()
    {
        Assert.AreEqual(0, LineIndex.CountLines(""));
        Assert.AreEqual(1, LineIndex.CountLines("a"));
        Assert.AreEqual(1, LineIndex.CountLines("a\n"));
        Assert.AreEqual(2, LineIndex.CountLines("a\nb"));
        Assert.AreEqual(3, LineIndex.CountLines("a\n\nb\n"));
        Assert.AreEqual(new LineIndex("a\n\nb\n").Count, LineIndex.CountLines("a\n\nb\n"));
    }
}
