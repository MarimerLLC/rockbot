using RockBot.UserProxy;

namespace RockBot.Host.Tests;

[TestClass]
public sealed class ReplyAttachmentBufferTests
{
    private static AgentAttachment Att(string path, string mime = "image/png")
        => new() { Mime = mime, Path = path };

    [TestMethod]
    public void Drain_ReturnsEmpty_WhenNothingStaged()
    {
        var buffer = new ReplyAttachmentBuffer();

        var drained = buffer.Drain("unseen", "t1");

        Assert.AreEqual(0, drained.Count);
    }

    [TestMethod]
    public void Add_Then_Drain_ReturnsStagedAttachments_InOrder()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "t1", Att("a.png"));
        buffer.Add("s1", "t1", Att("b.png"));

        var drained = buffer.Drain("s1", "t1");

        Assert.AreEqual(2, drained.Count);
        Assert.AreEqual("a.png", drained[0].Path);
        Assert.AreEqual("b.png", drained[1].Path);
    }

    [TestMethod]
    public void Drain_Clears_SoSecondDrainIsEmpty()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "t1", Att("a.png"));

        var first = buffer.Drain("s1", "t1");
        var second = buffer.Drain("s1", "t1");

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(0, second.Count);
    }

    [TestMethod]
    public void Sessions_AreIsolated()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("a", "t1", Att("a.png"));
        buffer.Add("b", "t1", Att("b.png"));

        var drainedA = buffer.Drain("a", "t1");

        Assert.AreEqual(1, drainedA.Count);
        Assert.AreEqual("a.png", drainedA[0].Path);
        // Draining a did not touch b.
        Assert.AreEqual(1, buffer.Drain("b", "t1").Count);
    }

    [TestMethod]
    public void ConcurrentProducers_DrainOnlyTheirOwnTurn()
    {
        // The item-1 bug: two producers stage under one primary session. Draining turn A's
        // final reply must return only A's attachments and leave B's intact for B's own drain.
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "turnA", Att("a.png"));
        buffer.Add("s1", "turnB", Att("b.png"));

        var drainedA = buffer.Drain("s1", "turnA");

        Assert.AreEqual(1, drainedA.Count);
        Assert.AreEqual("a.png", drainedA[0].Path);

        var drainedB = buffer.Drain("s1", "turnB");
        Assert.AreEqual(1, drainedB.Count);
        Assert.AreEqual("b.png", drainedB[0].Path);
    }

    [TestMethod]
    public void Clear_Session_DropsAllTurns()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "t1", Att("a.png"));
        buffer.Add("s1", "t2", Att("b.png"));

        buffer.Clear("s1");

        Assert.AreEqual(0, buffer.Drain("s1", "t1").Count);
        Assert.AreEqual(0, buffer.Drain("s1", "t2").Count);
    }

    [TestMethod]
    public void Clear_Turn_DropsOneTurn_LeavesSiblings()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "t1", Att("a.png"));
        buffer.Add("s1", "t2", Att("b.png"));

        buffer.Clear("s1", "t1");

        Assert.AreEqual(0, buffer.Drain("s1", "t1").Count);
        // Sibling turn untouched.
        Assert.AreEqual(1, buffer.Drain("s1", "t2").Count);
    }

    [TestMethod]
    public void Ttl_SweepsExpiredStage()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var buffer = new ReplyAttachmentBuffer(time, ttl: TimeSpan.FromMinutes(30));
        buffer.Add("s1", "t1", Att("a.png"));

        time.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, buffer.Drain("s1", "t1").Count, "Stage past TTL should be swept.");
    }

    [TestMethod]
    public void Ttl_KeepsStageWithinWindow()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var buffer = new ReplyAttachmentBuffer(time, ttl: TimeSpan.FromMinutes(30));
        buffer.Add("s1", "t1", Att("a.png"));

        time.Advance(TimeSpan.FromMinutes(29));

        Assert.AreEqual(1, buffer.Drain("s1", "t1").Count, "Stage within TTL should survive.");
    }

    [TestMethod]
    public void Ttl_RefreshedOnEachAdd()
    {
        // A second Add in the same turn refreshes LastStagedAt, so a long-running stage that
        // keeps adding within the window is never swept.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var buffer = new ReplyAttachmentBuffer(time, ttl: TimeSpan.FromMinutes(30));
        buffer.Add("s1", "t1", Att("a.png"));

        time.Advance(TimeSpan.FromMinutes(20));
        buffer.Add("s1", "t1", Att("b.png"));   // refreshes the stamp
        time.Advance(TimeSpan.FromMinutes(20));  // 40 min since first add, but only 20 since second

        var drained = buffer.Drain("s1", "t1");
        Assert.AreEqual(2, drained.Count, "Refreshed stage should survive past the original add's TTL.");
    }

    [TestMethod]
    public void DrainForFinalReply_ReturnsNull_WhenNotFinal()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "t1", Att("a.png"));

        Assert.IsNull(buffer.DrainForFinalReply("s1", "t1", isFinal: false));
        // The stage is untouched — a later final reply still drains it.
        Assert.AreEqual(1, buffer.Drain("s1", "t1").Count);
    }

    [TestMethod]
    public void DrainForFinalReply_ReturnsNull_WhenEmpty()
    {
        var buffer = new ReplyAttachmentBuffer();

        Assert.IsNull(buffer.DrainForFinalReply("s1", "t1", isFinal: true));
    }

    [TestMethod]
    public void DrainForFinalReply_ReturnsList_WhenFinalAndPresent()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "t1", Att("a.png"));

        var drained = buffer.DrainForFinalReply("s1", "t1", isFinal: true);

        Assert.IsNotNull(drained);
        Assert.AreEqual(1, drained!.Count);
        // Drained — a second final reply gets nothing.
        Assert.IsNull(buffer.DrainForFinalReply("s1", "t1", isFinal: true));
    }

    [TestMethod]
    public void SessionIds_AreCaseInsensitive()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("Session-A", "t1", Att("a.png"));

        Assert.AreEqual(1, buffer.Drain("session-a", "t1").Count);
    }

    [TestMethod]
    public void TurnIds_AreCaseInsensitive()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "Turn-A", Att("a.png"));

        Assert.AreEqual(1, buffer.Drain("s1", "turn-a").Count);
    }

    [TestMethod]
    public void Clear_Session_DropsStagedAttachments()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", "t1", Att("a.png"));

        buffer.Clear("s1");

        Assert.AreEqual(0, buffer.Drain("s1", "t1").Count);
    }

    /// <summary>Minimal fake <see cref="TimeProvider"/> for TTL testing without the test-package dependency.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
