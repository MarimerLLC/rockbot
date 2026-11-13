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

    private string ReadAttachment(AgentAttachment attachment) =>
        File.ReadAllText(Path.Combine(_attachments, attachment.Path.Replace('/', Path.DirectorySeparatorChar)));

    [TestMethod]
    public void ToolList_OffersAttachFile_BesideAttachImage()
    {
        var names = Tools().Tools.Select(t => t.Name).ToList();

        CollectionAssert.Contains(names, "attach_image");
        CollectionAssert.Contains(names, "attach_file");
    }

    [TestMethod]
    public async Task HtmlOutsideAttachments_IsCopiedIntoPreviews_AndStagedAsHtml()
    {
        Write("exports/deck.html", "<html>v1</html>");

        var result = await Tools().AttachFile("exports/deck.html");

        StringAssert.Contains(result, "Attached");
        var staged = Drain();
        Assert.AreEqual(1, staged.Count);
        Assert.AreEqual("text/html", staged[0].Mime);
        Assert.AreEqual("deck.html", staged[0].FileName, "the user sees the name the agent gave the file");

        // The reference is relative to the attachments directory, which is all a client serves.
        StringAssert.Matches(staged[0].Path, new System.Text.RegularExpressions.Regex(@"^previews/deck-[0-9a-f]{12}\.html$"));
        Assert.AreEqual("<html>v1</html>", ReadAttachment(staged[0]));
    }

    [TestMethod]
    public async Task ARevisedFile_GetsANewSnapshot_AndTheEarlierOneIsKept()
    {
        var source = Write("exports/deck.html", "<html>v1</html>");
        var tools = Tools();

        await tools.AttachFile("exports/deck.html");
        File.WriteAllText(source, "<html>v2</html>");
        await tools.AttachFile("exports/deck.html");

        var staged = Drain();
        Assert.AreEqual(2, staged.Count);
        Assert.AreNotEqual(staged[0].Path, staged[1].Path);
        Assert.AreEqual("<html>v1</html>", ReadAttachment(staged[0]), "an earlier reply keeps showing what it showed");
        Assert.AreEqual("<html>v2</html>", ReadAttachment(staged[1]));
    }

    [TestMethod]
    public async Task TheSameContentAttachedTwiceInATurn_IsStagedOnce()
    {
        Write("exports/deck.html");
        var tools = Tools();

        await tools.AttachFile("exports/deck.html");
        var second = await tools.AttachFile("exports/deck.html");

        StringAssert.Contains(second, "already attached");
        Assert.AreEqual(1, Drain().Count);
    }

    [TestMethod]
    public async Task UnchangedContent_ReusesTheCopy_AcrossTurns()
    {
        Write("exports/deck.html");

        await Tools().AttachFile("exports/deck.html");
        // A later turn builds its own tools; iterating on a deck must not pile up identical copies.
        var laterTurn = new AttachmentReplyTools(
            new AttachmentStorage(_attachments), _buffer, Session, "turn-2", NullLogger.Instance);
        await laterTurn.AttachFile("exports/deck.html");

        var previews = Directory.GetFiles(Path.Combine(_attachments, "previews"));
        Assert.AreEqual(1, previews.Length, "one copy per distinct content, and no temp files left behind");
        Assert.AreEqual(Drain()[0].Path, _buffer.Drain(Session, "turn-2")[0].Path);
    }

    [TestMethod]
    public async Task FileAlreadyUnderAttachments_IsReferencedInPlace()
    {
        Write("attachments/charts/q3.png", "png");

        var result = await Tools().AttachFile("attachments/charts/q3.png");

        StringAssert.Contains(result, "Attached");
        var staged = Drain();
        Assert.AreEqual("charts/q3.png", staged[0].Path);
        Assert.AreEqual("image/png", staged[0].Mime);
        Assert.IsFalse(Directory.Exists(Path.Combine(_attachments, "previews")), "no copy is needed");
    }

    [TestMethod]
    public async Task AbsolutePathOnTheSharedVolume_IsAccepted()
    {
        var full = Write("exports/deck.html");

        var result = await Tools().AttachFile(full);

        StringAssert.Contains(result, "Attached");
        Assert.AreEqual(1, Drain().Count);
    }

    [TestMethod]
    public async Task PathsResolveAgainstTheFileToolsRoot_WhenItIsNotTheAttachmentsParent()
    {
        // FileSystem:BasePath and ROCKBOT_SHARED_PATH are separate settings. file_write and
        // render_screenshot use the first, so attach_file must find "exports/deck.html" there too.
        var fileToolsRoot = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(Path.Combine(fileToolsRoot, "exports"));
        File.WriteAllText(Path.Combine(fileToolsRoot, "exports", "deck.html"), "<html>there</html>");
        var tools = new AttachmentReplyTools(
            new AttachmentStorage(_attachments), _buffer, Session, Turn, NullLogger.Instance,
            ClientCapabilityPresets.Blazor, sharedRoot: fileToolsRoot);

        var result = await tools.AttachFile("exports/deck.html");

        StringAssert.Contains(result, "Attached");
        Assert.AreEqual("<html>there</html>", ReadAttachment(Drain()[0]));
    }

    [TestMethod]
    public async Task FriendlyName_IsUsed()
    {
        Write("exports/deck.html");

        await Tools().AttachFile("exports/deck.html", fileName: "Q3 review deck");

        Assert.AreEqual("Q3 review deck", Drain()[0].FileName);
    }

    [TestMethod]
    [DataRow("../outside.html")]
    [DataRow("exports/../../outside.html")]
    public async Task Traversal_IsRejected(string path)
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_root)!, "outside.html"), "<html/>");

        var result = await Tools().AttachFile(path);

        StringAssert.Contains(result, "outside the shared volume");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    public async Task AbsolutePathOutsideTheSharedVolume_IsRejected()
    {
        // A sibling whose name merely starts with the root's must not pass a prefix check.
        var sibling = _root + "-sibling";
        Directory.CreateDirectory(sibling);
        var outside = Path.Combine(sibling, "deck.html");
        File.WriteAllText(outside, "<html/>");
        try
        {
            var result = await Tools().AttachFile(outside);

            StringAssert.Contains(result, "outside the shared volume");
            Assert.AreEqual(0, Drain().Count);
        }
        finally
        {
            try { Directory.Delete(sibling, recursive: true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task SymbolicLinkToAFileOutsideTheVolume_IsRejected_AndNothingIsCopied()
    {
        // A script pod can plant a link on the shared volume that points at the agent's own files.
        var secret = Path.Combine(Path.GetDirectoryName(_root)!, $"secret-{Guid.NewGuid():N}.html");
        File.WriteAllText(secret, "token");
        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(_root, "exports", "x.html"), secret);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Inconclusive($"Cannot create symbolic links here: {ex.Message}");
            }

            var result = await Tools().AttachFile("exports/x.html");

            StringAssert.Contains(result, "symbolic link");
            Assert.AreEqual(0, Drain().Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(_attachments, "previews")));
        }
        finally
        {
            try { File.Delete(secret); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public async Task MissingFile_IsRejected()
    {
        var result = await Tools().AttachFile("exports/nope.html");

        StringAssert.Contains(result, "no such file");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    public async Task EmptyPath_IsRejected()
    {
        var result = await Tools().AttachFile(" ");

        StringAssert.Contains(result, "no path");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    [DataRow("exports/deck.md")]
    [DataRow("exports/run.py")]
    [DataRow("exports/noextension")]
    public async Task TypeTheUserCannotBeShown_IsRejected_AndNamesTheAllowedTypes(string path)
    {
        Write(path, "# Slide one");

        var result = await Tools().AttachFile(path);

        StringAssert.Contains(result, "Allowed types");
        StringAssert.Contains(result, ".html");
        Assert.AreEqual(0, Drain().Count, "raw Markdown is source, not a deck (#669)");
    }

    [TestMethod]
    public async Task OversizeFile_IsRejected()
    {
        var full = Write("exports/huge.html");
        using (var stream = new FileStream(full, FileMode.Open, FileAccess.Write))
            stream.SetLength(AttachmentReplyTools.MaxAttachFileBytes + 1);

        var result = await Tools().AttachFile("exports/huge.html");

        StringAssert.Contains(result, "limit");
        Assert.AreEqual(0, Drain().Count);
    }

    [TestMethod]
    public async Task Html_OnAClientThatPreviews_SaysTheUserSeesAPreview()
    {
        Write("exports/deck.html");

        var result = await Tools(ClientCapabilityPresets.Blazor).AttachFile("exports/deck.html");

        StringAssert.Contains(result, "live preview");
    }

    [TestMethod]
    public async Task Html_OnAClientThatCannotPreview_TellsTheAgentToGiveAnotherWay()
    {
        Write("exports/deck.html");

        var result = await Tools(ClientCapabilityPresets.Cli).AttachFile("exports/deck.html");

        StringAssert.Contains(result, "cannot preview HTML");
        StringAssert.Contains(result, "cloud storage");
        Assert.AreEqual(1, Drain().Count, "the attachment still rides the reply as a placeholder");
    }

    [TestMethod]
    public async Task Pdf_OnAClientWithAttachments_IsDescribedAsALink()
    {
        Write("exports/report.pdf", "%PDF-1.7");

        var result = await Tools(ClientCapabilityPresets.Blazor).AttachFile("exports/report.pdf");

        StringAssert.Contains(result, "link");
        Assert.AreEqual("application/pdf", Drain()[0].Mime);
    }
}
