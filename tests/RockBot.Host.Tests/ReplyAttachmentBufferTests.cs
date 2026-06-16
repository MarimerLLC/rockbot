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

        var drained = buffer.Drain("unseen");

        Assert.AreEqual(0, drained.Count);
    }

    [TestMethod]
    public void Add_Then_Drain_ReturnsStagedAttachments_InOrder()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", Att("a.png"));
        buffer.Add("s1", Att("b.png"));

        var drained = buffer.Drain("s1");

        Assert.AreEqual(2, drained.Count);
        Assert.AreEqual("a.png", drained[0].Path);
        Assert.AreEqual("b.png", drained[1].Path);
    }

    [TestMethod]
    public void Drain_Clears_SoSecondDrainIsEmpty()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", Att("a.png"));

        var first = buffer.Drain("s1");
        var second = buffer.Drain("s1");

        Assert.AreEqual(1, first.Count);
        Assert.AreEqual(0, second.Count);
    }

    [TestMethod]
    public void Sessions_AreIsolated()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("a", Att("a.png"));
        buffer.Add("b", Att("b.png"));

        var drainedA = buffer.Drain("a");

        Assert.AreEqual(1, drainedA.Count);
        Assert.AreEqual("a.png", drainedA[0].Path);
        // Draining a did not touch b.
        Assert.AreEqual(1, buffer.Drain("b").Count);
    }

    [TestMethod]
    public void SessionIds_AreCaseInsensitive()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("Session-A", Att("a.png"));

        Assert.AreEqual(1, buffer.Drain("session-a").Count);
    }

    [TestMethod]
    public void Clear_DropsStagedAttachments()
    {
        var buffer = new ReplyAttachmentBuffer();
        buffer.Add("s1", Att("a.png"));

        buffer.Clear("s1");

        Assert.AreEqual(0, buffer.Drain("s1").Count);
    }
}
