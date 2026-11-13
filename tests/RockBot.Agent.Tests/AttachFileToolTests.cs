using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests;

/// <summary>
/// <c>attach_file</c> (#669): the way an HTML deck written to <c>exports/</c> reaches a client
/// that can preview it.
/// </summary>
[TestClass]
public sealed class AttachFileToolTests
{
    private const string Session = "session-1";
    private const string Turn = "turn-1";

    private string _root = null!;
    private string _attachments = null!;
    private ReplyAttachmentBuffer _buffer = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "rb-attach-file-tests", Guid.NewGuid().ToString("N"));
        _attachments = Path.Combine(_root, "attachments");
        Directory.CreateDirectory(Path.Combine(_root, "exports"));
        _buffer = new ReplyAttachmentBuffer();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private AttachmentReplyTools Tools(ClientCapabilities capabilities = ClientCapabilityPresets.Blazor) =>
        new(new AttachmentStorage(_attachments), _buffer, Session, Turn, NullLogger.Instance, capabilities);

    private string Write(string relativePath, string content = "<html><body>deck</body></html>")
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private IReadOnlyList<AgentAttachment> Drain() => _buffer.Drain(Session, Turn);

    [TestMethod]
    public void ToolList_OffersAttachFile_BesideAttachImage()
    {
        var names = Tools().Tools.Select(t => t.Name).ToList();

        CollectionAssert.Contains(names, "attach_image");
        CollectionAssert.Contains(names, "attach_file");
    }

    [TestMethod]
    public void HtmlOutsideAttachments_IsCopiedIntoPreviews_AndStagedAsHtml()
    {
        Write("exports/deck.html", "<html>v1</html>");

        var result = Tools().AttachFile("exports/deck.html");

        StringAssert.Contains(result, "Attached");
        var staged = Drain();
        Assert.AreEqual(1, staged.Count);
        Assert.AreEqual("text/html", staged[0].Mime);
        Assert.AreEqual("deck.html", staged[0].FileName);

        // The reference is relative to the attachments directory, which is all a client serves.
        StringAssert.StartsWith(staged[0].Path, "previews/");
        StringAssert.EndsWith(staged[0].Path, "/deck.html");
        var copy = Path.Combine(_attachments, staged[0].Path.Replace('/', Path.DirectorySeparatorChar));
        Assert.AreEqual("<html>v1</html>", File.ReadAllText(copy));
    }

    [TestMethod]
    public void AttachingTwice_KeepsBothSnapshots()
    {
        var source = Write("exports/deck.html", "<html>v1</html>");
        var tools = Tools();

        tools.AttachFile("exports/deck.html");
        File.WriteAllText(source, "<html>v2</html>");
        tools.AttachFile("exports/deck.html");

        var staged = Drain();
        Assert.AreEqual(2, staged.Count);
        Assert.AreNotEqual(staged[0].Path, staged[1].Path);
        string Read(AgentAttachment a) =>
            File.ReadAllText(Path.Combine(_attachments, a.Path.Replace('/', Path.DirectorySeparatorChar)));
        Assert.AreEqual("<html>v1</html>", Read(staged[0]), "an earlier reply keeps showing what it showed");
        Assert.AreEqual("<html>v2</html>", Read(staged[1]));
    }

    [TestMethod]
    public void FileAlreadyUnderAttachments_IsReferencedInPlace()
    {
        Write("attachments/charts/q3.png", "png");

        var result = Tools().AttachFile("attachments/charts/q3.png");

        StringAssert.Contains(result, "Attached");
        var staged = Drain();
        Assert.AreEqual("charts/q3.png", staged[0].Path);
        Assert.AreEqual("image/png", staged[0].Mime);
        Assert.IsFalse(Directory.Exists(Path.Combine(_attachments, "previews")), "no copy is needed");
    }

    [TestMethod]
    public void AbsolutePathOnTheSharedVolume_IsAccepted()
    {
        var full = Write("exports/deck.html");

        var result = Tools().AttachFile(full);

        StringAssert.Contains(result, "Attached");
        Assert.AreEqual(1, Drain().Count);
    }

    [TestMethod]
    public void FriendlyName_IsUsed()
    {
        Write("exports/deck.html");

        Tools().AttachFile("exports/deck.html", fileName: "Q3 review deck");

        Assert.AreEqual("Q3 review deck", Drain()[0].FileName);
    }

    [TestMethod]
    [DataRow("../outside.html")]
    [DataRow("exports/../../outside.html")]
    public void Traversal_IsRejected(string path)
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "outside.html"), "<html/>");

        var result = Tools().AttachFile(path);

        StringAssert.Contains(result, "outside the shared volume");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    public void AbsolutePathOutsideTheSharedVolume_IsRejected()
    {
        // A sibling whose name merely starts with the root's must not pass a prefix check.
        var sibling = _root + "-sibling";
        Directory.CreateDirectory(sibling);
        var outside = Path.Combine(sibling, "deck.html");
        File.WriteAllText(outside, "<html/>");
        try
        {
            var result = Tools().AttachFile(outside);

            StringAssert.Contains(result, "outside the shared volume");
            Assert.AreEqual(0, Drain().Count);
        }
        finally
        {
            try { Directory.Delete(sibling, recursive: true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void MissingFile_IsRejected()
    {
        var result = Tools().AttachFile("exports/nope.html");

        StringAssert.Contains(result, "no such file");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    public void EmptyPath_IsRejected()
    {
        var result = Tools().AttachFile(" ");

        StringAssert.Contains(result, "no path");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    [DataRow("exports/deck.md")]
    [DataRow("exports/run.py")]
    [DataRow("exports/noextension")]
    public void TypeTheUserCannotBeShown_IsRejected_AndNamesTheAllowedTypes(string path)
    {
        Write(path, "# Slide one");

        var result = Tools().AttachFile(path);

        StringAssert.Contains(result, "Allowed types");
        StringAssert.Contains(result, ".html");
        Assert.AreEqual(0, Drain().Count, "raw Markdown is source, not a deck (#669)");
    }

    [TestMethod]
    public void OversizeFile_IsRejected()
    {
        var full = Write("exports/huge.html");
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Write))
            stream.SetLength(AttachmentReplyTools.MaxAttachFileBytes + 1);

        var result = Tools().AttachFile("exports/huge.html");

        StringAssert.Contains(result, "limit");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    public void Html_OnAClientThatPreviews_SaysTheUserSeesAPreview()
    {
        Write("exports/deck.html");

        var result = Tools(ClientCapabilityPresets.Blazor).AttachFile("exports/deck.html");

        StringAssert.Contains(result, "live preview");
    }

    [TestMethod]
    public void Html_OnAClientThatCannotPreview_TellsTheAgentToGiveAnotherWay()
    {
        Write("exports/deck.html");

        var result = Tools(ClientCapabilityPresets.Cli).AttachFile("exports/deck.html");

        StringAssert.Contains(result, "cannot preview HTML");
        StringAssert.Contains(result, "cloud storage");
        Assert.AreEqual(1, Drain().Count, "the attachment still rides the reply as a placeholder");
    }

    [TestMethod]
    public void Pdf_OnAClientWithAttachments_IsDescribedAsALink()
    {
        Write("exports/report.pdf", "%PDF-1.7");

        var result = Tools(ClientCapabilityPresets.Blazor).AttachFile("exports/report.pdf");

        StringAssert.Contains(result, "link");
        Assert.AreEqual("application/pdf", Drain()[0].Mime);
    }
}
