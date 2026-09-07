using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using RockBot.Host;

namespace RockBot.Host.Tests;

/// <summary>
/// Tests for putting a user's attachments in front of the model (issue #565). Bytes can only
/// enter a conversation as content parts on a user message on OpenAI-compatible APIs, so this
/// appends to the last user message. The behaviour that matters most is the negative one: a file
/// the model cannot be shown is announced by path, never silently dropped.
/// </summary>
[TestClass]
public class InboundAttachmentInjectorTests
{
    private static readonly ConversationTurnAttachment Png =
        new("image/png", "shot.png", "screenshot.png");

    [TestMethod]
    public async Task Inject_ImageOnSeeingTier_AppendsDataContentToTheLastUserMessage()
    {
        var messages = BuildContext();

        await InboundAttachmentInjector.InjectAsync(
            messages, [Png], tierCanSee: true, ReadsBytes([1, 2, 3]),
            NullLogger.Instance);

        var user = messages[^1];
        Assert.AreEqual(ChatRole.User, user.Role);
        var data = user.Contents.OfType<DataContent>().SingleOrDefault();
        Assert.IsNotNull(data, "A seeing tier must be handed the image itself.");
        Assert.AreEqual("image/png", data!.MediaType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, data.Data.ToArray());
    }

    [TestMethod]
    public async Task Inject_ImageOnSeeingTier_LeavesEveryOtherMessageUntouched()
    {
        var messages = BuildContext();
        var systemBefore = messages[0].Contents.Count;
        var historyBefore = messages[1].Contents.Count;

        await InboundAttachmentInjector.InjectAsync(
            messages, [Png], tierCanSee: true, ReadsBytes([1]), NullLogger.Instance);

        Assert.AreEqual(3, messages.Count, "Injection must not add or remove messages.");
        Assert.AreEqual(systemBefore, messages[0].Contents.Count);
        Assert.AreEqual(historyBefore, messages[1].Contents.Count,
            "An earlier user turn must not receive this turn's image — only the current turn does.");
    }

    [TestMethod]
    public async Task Inject_BlindTier_AnnouncesThePathAndSendsNoBytes()
    {
        var messages = BuildContext();
        var read = false;

        await InboundAttachmentInjector.InjectAsync(
            messages, [Png], tierCanSee: false,
            (_, _) => { read = true; return Task.FromResult<byte[]>([1]); },
            NullLogger.Instance);

        var user = messages[^1];
        Assert.IsFalse(user.Contents.OfType<DataContent>().Any(),
            "A model that cannot see must never be sent image bytes — the provider rejects the whole request.");
        Assert.IsFalse(read, "Nothing should be read from disk when the bytes cannot be used.");

        var marker = user.Contents.OfType<TextContent>().Last().Text!;
        StringAssert.Contains(marker, "attachments/shot.png");
        StringAssert.Contains(marker, "analyze_file",
            "The model needs to be told how to reach the file, not just that one exists.");
    }

    [TestMethod]
    public async Task Inject_NonImage_IsAnnouncedByPathEvenOnASeeingTier()
    {
        var messages = BuildContext();
        var pdf = new ConversationTurnAttachment("application/pdf", "invoice.pdf", null);

        await InboundAttachmentInjector.InjectAsync(
            messages, [pdf], tierCanSee: true, ReadsBytes([1]), NullLogger.Instance);

        Assert.IsFalse(messages[^1].Contents.OfType<DataContent>().Any(),
            "Only images ride inline; a PDF is announced so the model can call analyze_file.");
        StringAssert.Contains(messages[^1].Contents.OfType<TextContent>().Last().Text!, "invoice.pdf");
    }

    [TestMethod]
    public async Task Inject_UnreadableFile_DegradesToTheMarkerRatherThanFailingTheTurn()
    {
        var messages = BuildContext();

        await InboundAttachmentInjector.InjectAsync(
            messages, [Png], tierCanSee: true,
            (_, _) => throw new FileNotFoundException("gone"),
            NullLogger.Instance);

        Assert.IsFalse(messages[^1].Contents.OfType<DataContent>().Any());
        var marker = messages[^1].Contents.OfType<TextContent>().Last().Text!;
        StringAssert.Contains(marker, "could not be read",
            "The model should know the difference between 'you cannot see' and 'the file is gone'.");
    }

    [TestMethod]
    public async Task Inject_SeveralAttachments_AllReachTheMessage()
    {
        var messages = BuildContext();
        var second = new ConversationTurnAttachment("image/jpeg", "photo.jpg", null);

        await InboundAttachmentInjector.InjectAsync(
            messages, [Png, second], tierCanSee: true, ReadsBytes([1]), NullLogger.Instance);

        Assert.AreEqual(2, messages[^1].Contents.OfType<DataContent>().Count());
    }

    [TestMethod]
    public async Task Inject_NoAttachments_LeavesTheContextByteIdentical()
    {
        var messages = BuildContext();
        var before = messages.Select(m => m.Contents.Count).ToList();

        await InboundAttachmentInjector.InjectAsync(
            messages, [], tierCanSee: true, ReadsBytes([1]), NullLogger.Instance);

        CollectionAssert.AreEqual(before, messages.Select(m => m.Contents.Count).ToList());
    }

    [TestMethod]
    public async Task Inject_NoUserMessage_DoesNotThrow()
    {
        // Defensive: every real context ends with the user's turn, but a caller that assembled
        // one differently should get a warning, not an exception mid-turn.
        var messages = new List<ChatMessage> { new(ChatRole.System, "prompt") };

        await InboundAttachmentInjector.InjectAsync(
            messages, [Png], tierCanSee: true, ReadsBytes([1]), NullLogger.Instance);

        Assert.AreEqual(1, messages.Count);
        Assert.IsFalse(messages[0].Contents.OfType<DataContent>().Any());
    }

    [TestMethod]
    public void BuildMarker_FallsBackToThePathWhenThereIsNoFriendlyName()
    {
        var marker = InboundAttachmentInjector.BuildMarker(
            new ConversationTurnAttachment("image/png", "a1b2.png", null), unreadable: false);

        StringAssert.Contains(marker, "a1b2.png");
    }

    private static Func<string, CancellationToken, Task<byte[]>> ReadsBytes(byte[] bytes) =>
        (_, _) => Task.FromResult(bytes);

    /// <summary>System prompt, one prior user turn, then the current user turn.</summary>
    private static List<ChatMessage> BuildContext() =>
    [
        new(ChatRole.System, "system prompt"),
        new(ChatRole.User, "an earlier question"),
        new(ChatRole.User, "what is in this picture?"),
    ];

    // ── What survives into later turns ───────────────────────────────────────

    [TestMethod]
    public void DescribeAttachments_NamesThePathSoALaterTurnCanStillReachTheFile()
    {
        // Regression for a defect a live Blazor session found: on a seeing tier the image is
        // injected and no marker is added, so the path never entered the persisted turn. On the
        // next turn the agent answered "I don't have the image in this chat to re-check" and
        // called file_list looking for it — it had neither the image nor the filename.
        var described = InboundAttachmentInjector.DescribeAttachments([Png]);

        StringAssert.Contains(described, "attachments/shot.png",
            "The path is the only handle a later turn has; analyze_file needs it by name.");
        StringAssert.Contains(described, "screenshot.png");
        StringAssert.Contains(described, "image/png");
    }

    [TestMethod]
    public void DescribeAttachments_NoAttachments_IsEmptySoCallersCanConcatenateBlindly()
    {
        Assert.AreEqual(string.Empty, InboundAttachmentInjector.DescribeAttachments(null));
        Assert.AreEqual(string.Empty, InboundAttachmentInjector.DescribeAttachments([]));
    }

    [TestMethod]
    public void DescribeAttachments_SeveralFiles_NamesEachOnItsOwnLine()
    {
        var described = InboundAttachmentInjector.DescribeAttachments(
            [Png, new ConversationTurnAttachment("application/pdf", "invoice.pdf", null)]);

        Assert.AreEqual(2, described.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        StringAssert.Contains(described, "attachments/invoice.pdf");
    }
}
