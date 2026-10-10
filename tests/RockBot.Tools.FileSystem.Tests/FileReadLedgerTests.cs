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
    public void FindFullyKnownCopy_ReturnsAnotherFullySeenPathWithTheSameHash()
    {
        var ledger = new FileReadLedger();
        ledger.RecordFullyKnown("s", "/vol/attachments/a.md", "h1", 3);

        Assert.AreEqual("/vol/attachments/a.md", ledger.FindFullyKnownCopy("s", "h1", excludePath: "/vol/drafts/a.md"));
        Assert.IsNull(ledger.FindFullyKnownCopy("s", "h2"), "Different content.");
        Assert.IsNull(ledger.FindFullyKnownCopy("other", "h1"), "Different session.");
        Assert.IsNull(ledger.FindFullyKnownCopy("s", "h1", excludePath: "/vol/attachments/a.md"));
    }

    [TestMethod]
    public void FindFullyKnownCopy_IgnoresPartialCoverage()
    {
        var ledger = new FileReadLedger();
        ledger.RecordRead("s", "/vol/a.md", "h1", 10, 1, 5);
        Assert.IsNull(ledger.FindFullyKnownCopy("s", "h1"));

        ledger.RecordRead("s", "/vol/a.md", "h1", 10, 6, 10);
        Assert.AreEqual("/vol/a.md", ledger.FindFullyKnownCopy("s", "h1"));
    }

    [TestMethod]
    public void FindFullyKnownCopy_ForgetsReplacedForgottenAndEvictedEntries()
    {
        var ledger = new FileReadLedger(maxEntries: 4);
        ledger.RecordFullyKnown("s", "/vol/a.md", "h1", 1);

        // A newer version of the same path replaces the old hash.
        ledger.RecordFullyKnown("s", "/vol/a.md", "h2", 1);
        Assert.IsNull(ledger.FindFullyKnownCopy("s", "h1"));
        Assert.AreEqual("/vol/a.md", ledger.FindFullyKnownCopy("s", "h2"));

        ledger.Forget("s", "/vol/a.md");
        Assert.IsNull(ledger.FindFullyKnownCopy("s", "h2"));

        ledger.RecordFullyKnown("s", "/vol/old.md", "h-old", 1);
        for (var i = 0; i < 5; i++)
            ledger.RecordFullyKnown("s", $"/vol/f{i}", $"h{i + 10}", 1);
        Assert.IsNull(ledger.FindFullyKnownCopy("s", "h-old"), "Evicted entries leave the index too.");

        Assert.AreEqual(ledger.Count, ledger.HashIndexPathCount, "The index mirrors the entries exactly.");
    }

    [TestMethod]
    public void HashIndex_StaysInStepWithEntriesUnderChurn()
    {
        var ledger = new FileReadLedger(maxEntries: 8);
        var random = new Random(677);
        for (var i = 0; i < 2000; i++)
        {
            var session = $"s{random.Next(3)}";
            var path = $"/vol/f{random.Next(20)}";
            var hash = $"h{random.Next(5)}";
            switch (random.Next(3))
            {
                case 0: ledger.RecordFullyKnown(session, path, hash, 4); break;
                case 1: ledger.RecordRead(session, path, hash, 4, 1, 2); break;
                default: ledger.Forget(session, path); break;
            }

            Assert.AreEqual(ledger.Count, ledger.HashIndexPathCount, $"After step {i}.");
        }

        Assert.IsTrue(ledger.Count <= 8);
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
