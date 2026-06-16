using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;

namespace RockBot.Agent.Tests;

[TestClass]
public sealed class AttachmentReplyToolsTests
{
    private string _baseDir = null!;
    private AttachmentStorage _storage = null!;
    private ReplyAttachmentBuffer _buffer = null!;
    private AttachmentReplyTools _tools = null!;

    [TestInitialize]
    public void Init()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "rb-attach-tests", Guid.NewGuid().ToString("N"));
        _storage = new AttachmentStorage(_baseDir); // creates the directory
        _buffer = new ReplyAttachmentBuffer();
        _tools = new AttachmentReplyTools(_storage, _buffer, "session-1", NullLogger.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch { /* best effort */ }
    }

    private void WriteFile(string name, byte[]? bytes = null)
        => File.WriteAllBytes(Path.Combine(_baseDir, name), bytes ?? [1, 2, 3]);

    [TestMethod]
    public void AttachImage_RecordsIntoBuffer_ForExistingFile()
    {
        WriteFile("chart.png");

        var result = _tools.AttachImage("chart.png");

        StringAssert.Contains(result, "Attached");
        var drained = _buffer.Drain("session-1");
        Assert.AreEqual(1, drained.Count);
        Assert.AreEqual("chart.png", drained[0].Path);
        Assert.AreEqual("image/png", drained[0].Mime);
        Assert.AreEqual("chart.png", drained[0].FileName);
    }

    [TestMethod]
    public void AttachImage_InfersMime_FromExtension()
    {
        WriteFile("photo.jpeg");

        _tools.AttachImage("photo.jpeg");

        Assert.AreEqual("image/jpeg", _buffer.Drain("session-1")[0].Mime);
    }

    [TestMethod]
    public void AttachImage_UsesExplicitMime_WhenProvided()
    {
        WriteFile("diagram.bin");

        _tools.AttachImage("diagram.bin", mime: "image/svg+xml");

        Assert.AreEqual("image/svg+xml", _buffer.Drain("session-1")[0].Mime);
    }

    [TestMethod]
    public void AttachImage_UsesFriendlyName_WhenProvided()
    {
        WriteFile("chart.png");

        _tools.AttachImage("chart.png", fileName: "Quarterly results");

        Assert.AreEqual("Quarterly results", _buffer.Drain("session-1")[0].FileName);
    }

    [TestMethod]
    public void AttachImage_Rejects_NonExistentFile()
    {
        var result = _tools.AttachImage("missing.png");

        StringAssert.Contains(result, "no such file");
        Assert.AreEqual(0, _buffer.Drain("session-1").Count);
    }

    [TestMethod]
    public void AttachImage_Rejects_TraversalOutsideBase()
    {
        var result = _tools.AttachImage("../escape.png");

        StringAssert.Contains(result, "outside");
        Assert.AreEqual(0, _buffer.Drain("session-1").Count);
    }

    [TestMethod]
    public void AttachImage_Rejects_AbsolutePathOutsideBase()
    {
        var outside = Path.Combine(Path.GetTempPath(), "rb-attach-outside.png");
        File.WriteAllBytes(outside, [1, 2, 3]);
        try
        {
            var result = _tools.AttachImage(outside);

            StringAssert.Contains(result, "outside");
            Assert.AreEqual(0, _buffer.Drain("session-1").Count);
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void AttachImage_Rejects_EmptyPath()
    {
        var result = _tools.AttachImage("");

        StringAssert.Contains(result, "no path");
        Assert.AreEqual(0, _buffer.Drain("session-1").Count);
    }

    [TestMethod]
    public void AttachImage_StripsRedundantAttachmentsLeaf()
    {
        // When BasePath ends in /attachments, the model may still say "attachments/chart.png".
        // The leaf-strip must resolve that to <base>/chart.png, not <base>/attachments/chart.png.
        var dir = Path.Combine(Path.GetTempPath(), "rb-attach-leaf", Guid.NewGuid().ToString("N"), "attachments");
        var storage = new AttachmentStorage(dir);
        var buffer = new ReplyAttachmentBuffer();
        var tools = new AttachmentReplyTools(storage, buffer, "s", NullLogger.Instance);
        File.WriteAllBytes(Path.Combine(dir, "chart.png"), [1, 2, 3]);
        try
        {
            var result = tools.AttachImage("attachments/chart.png");

            StringAssert.Contains(result, "Attached");
            var drained = buffer.Drain("s");
            Assert.AreEqual(1, drained.Count);
            Assert.AreEqual("chart.png", drained[0].Path);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); } catch { /* best effort */ }
        }
    }
}
