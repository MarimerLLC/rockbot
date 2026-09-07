using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.UserProxy;

namespace RockBot.Agent.Tests.Attachments;

/// <summary>
/// Rules for the inbound half of the attachment story (issue #565). A person's upload reaches
/// the agent as bytes — over HTTP, or over the bus when the client cannot reach the endpoint —
/// and the agent writes it to the shared volume on their behalf, because frontends mount that
/// volume read-only and the CLI does not mount it at all.
/// </summary>
/// <remarks>
/// These rules live in one service precisely so the two transports cannot drift: an allowlist
/// enforced on the HTTP path but not the bus path would be no allowlist at all.
/// </remarks>
[TestClass]
public class InboundAttachmentServiceTests
{
    private string _root = null!;
    private InboundAttachmentService _service = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "rockbot-upload-tests", Guid.NewGuid().ToString("N"));
        _service = new InboundAttachmentService(
            new AttachmentStorage(_root),
            NullLogger<InboundAttachmentService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task Store_ValidPng_WritesFileAndReturnsRelativePathReference()
    {
        var result = await Store("shot.png", "image/png", [1, 2, 3, 4]);

        Assert.IsTrue(result.Success, result.Error);
        Assert.IsNotNull(result.Attachment);
        Assert.AreEqual("image/png", result.Attachment!.Mime);
        Assert.AreEqual("shot.png", result.Attachment.Path,
            "The wire carries a relative reference; the receiving side resolves it under its own base.");
        Assert.AreEqual("shot.png", result.Attachment.FileName);
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(_root, "shot.png")));
    }

    [TestMethod]
    public async Task Store_SecondFileWithTheSameName_DoesNotOverwriteTheFirst()
    {
        await Store("shot.png", "image/png", [1]);
        var second = await Store("shot.png", "image/png", [2]);

        Assert.IsTrue(second.Success);
        Assert.AreNotEqual("shot.png", second.Attachment!.Path,
            "AttachmentStorage resolves the collision; two people sending 'screenshot.png' must not clobber.");
        Assert.AreEqual("shot.png", second.Attachment.FileName,
            "The user still sees the name they chose, even though it was stored under another.");
        CollectionAssert.AreEqual(new byte[] { 1 }, await File.ReadAllBytesAsync(Path.Combine(_root, "shot.png")));
    }

    [TestMethod]
    public async Task Store_DisallowedType_IsRejectedWithAnActionableMessage()
    {
        var result = await Store("payload.exe", "application/x-msdownload", [1]);

        Assert.IsFalse(result.Success);
        Assert.IsNull(result.Attachment);
        StringAssert.Contains(result.Error!, "not accepted");
        StringAssert.Contains(result.Error!, "image/png",
            "The rejection has to name what is accepted, or the user is left guessing.");
        Assert.AreEqual(0, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task Store_TypeDisagreeingWithExtension_IsRejected()
    {
        // Storing this would leave a file whose name says one thing and whose contents say
        // another — the next reader (analyze_file, the vision call) resolves it by extension.
        var result = await Store("invoice.png", "application/pdf", [1]);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "does not look like");
        Assert.AreEqual(0, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task Store_JpegAcceptsBothExtensions()
    {
        Assert.IsTrue((await Store("a.jpg", "image/jpeg", [1])).Success);
        Assert.IsTrue((await Store("b.jpeg", "image/jpeg", [1])).Success);
    }

    [TestMethod]
    public async Task Store_OverTheSizeCap_IsRejectedBeforeTouchingDisk()
    {
        var oversized = new byte[InboundAttachmentService.MaxBytes + 1];

        var result = await Store("huge.png", "image/png", oversized);

        Assert.IsFalse(result.Success);
        StringAssert.Contains(result.Error!, "limit");
        Assert.AreEqual(0, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task Store_EmptyFile_IsRejected()
    {
        var result = await Store("empty.png", "image/png", []);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(0, Directory.GetFiles(_root).Length);
    }

    [TestMethod]
    public async Task Store_TraversalInTheFileName_StaysInsideTheAttachmentsDirectory()
    {
        // The client supplies a name, never a path. AttachmentStorage sanitises to the leaf, and
        // this pins that the upload door inherits that property rather than bypassing it.
        var result = await Store("../../escaped.png", "image/png", [1]);

        Assert.IsTrue(result.Success, result.Error);
        var written = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.AreEqual(1, written.Length);
        StringAssert.StartsWith(Path.GetFullPath(written[0]), Path.GetFullPath(_root),
            "A traversal in the supplied name must not place the file outside the base directory.");
    }

    private Task<AttachmentUploadResponse> Store(string fileName, string mime, byte[] data) =>
        _service.StoreAsync(fileName, mime, data, "sess-1", default);
}
