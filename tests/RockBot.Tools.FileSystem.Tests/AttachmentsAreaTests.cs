namespace RockBot.Tools.FileSystem.Tests;

/// <summary>
/// Issue #677: a write the filesystem refuses inside attachments/ explains the area and
/// points at the matching drafts/ file.
/// </summary>
[TestClass]
public class AttachmentsAreaTests
{
    private const string Deck = "# deck\n";

    private FileToolsFixture _fx = null!;

    [TestInitialize]
    public void Setup() => _fx = new FileToolsFixture();

    [TestCleanup]
    public void Cleanup() => _fx.Dispose();

    [TestMethod]
    public void Contains_IsTrueOnlyInsideTheAttachmentsDirectory()
    {
        Assert.IsTrue(AttachmentsArea.Contains(_fx.Options, _fx.FullPath("attachments/deck.md")));
        Assert.IsTrue(AttachmentsArea.Contains(_fx.Options, _fx.FullPath("attachments/sub/deck.md")));
        Assert.IsFalse(AttachmentsArea.Contains(_fx.Options, _fx.FullPath("drafts/deck.md")));
        Assert.IsFalse(AttachmentsArea.Contains(_fx.Options, _fx.FullPath("attachments-old/deck.md")),
            "A sibling directory that merely starts with the name is not the attachments area.");
    }

    [TestMethod]
    public void Contains_IsFalseWhenDisabled()
    {
        _fx.Options.AttachmentsDirectory = "";
        Assert.IsFalse(AttachmentsArea.Contains(_fx.Options, _fx.FullPath("attachments/deck.md")));
    }

    [TestMethod]
    public void Describe_WithMatchingDraft_NamesIt()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck);
        _fx.WriteRaw("exports/deck.md", Deck);

        var message = AttachmentsArea.Describe(_fx.Options, "attachments/deck.md", "Access to the path is denied.");

        StringAssert.Contains(message,
            "attachments/ holds downloaded files and is read-only for file tools.");
        StringAssert.Contains(message, "Edit the matching file under drafts/ instead: drafts/deck.md exists");
        StringAssert.Contains(message, "copy the content to a new drafts/ path and work there");
        StringAssert.Contains(message, "(System error: Access to the path is denied.)");
    }

    [TestMethod]
    public void Describe_WithoutMatchingDraft_SuggestsTheDraftsPath()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);

        var message = AttachmentsArea.Describe(_fx.Options, "attachments/deck.md", systemError: null);

        StringAssert.Contains(message,
            "attachments/ holds downloaded files and is read-only for file tools. Edit the matching file under "
            + "drafts/ (e.g. drafts/deck.md) if one exists, or copy the content to a new drafts/ path and work there.");
        Assert.IsFalse(message.Contains("System error"));
    }

    [TestMethod]
    public void FindMatchingDraft_IgnoresAttachmentsAndBackups_AndPrefersDrafts()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("attachments/old/deck.md", Deck);
        _fx.WriteRaw(".prev/drafts/deck.md", Deck);
        Assert.IsNull(AttachmentsArea.FindMatchingDraft(_fx.Options, "attachments/deck.md"));

        _fx.WriteRaw("exports/deck.md", Deck);
        Assert.AreEqual("exports/deck.md", AttachmentsArea.FindMatchingDraft(_fx.Options, "attachments/deck.md"));

        _fx.WriteRaw("drafts/talks/deck.md", Deck);
        Assert.AreEqual("drafts/talks/deck.md", AttachmentsArea.FindMatchingDraft(_fx.Options, "attachments/deck.md"));
    }

    [TestMethod]
    public void IsAccessDenied_RecognizesPermissionFailures()
    {
        Assert.IsTrue(WriteAccess.IsAccessDenied(new UnauthorizedAccessException("Access to the path '/x' is denied.")));
        Assert.IsTrue(WriteAccess.IsAccessDenied(new IOException("Read-only file system : '/x'")));
        Assert.IsFalse(WriteAccess.IsAccessDenied(new IOException("No space left on device")));
        Assert.IsFalse(WriteAccess.IsAccessDenied(new InvalidOperationException("denied")));
    }

    [TestMethod]
    public async Task Write_ReadOnlyFileInAttachments_ExplainsAndPointsAtDrafts()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck);
        if (!_fx.TryMakeReadOnly("attachments/deck.md"))
            Assert.Inconclusive("The test user can write read-only files (root?); permission cannot be simulated.");
        await _fx.ReadAsync("s1", "attachments/deck.md");

        var response = await _fx.WriteAsync("s1", "attachments/deck.md", "# revised\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "attachments/ holds downloaded files and is read-only for file tools.");
        StringAssert.Contains(response.Content!, "drafts/deck.md exists");
        Assert.AreEqual(Deck, _fx.ReadRaw("attachments/deck.md"));
        Assert.IsFalse(File.Exists(_fx.FullPath(".prev/attachments/deck.md")),
            "A write that cannot happen must not leave a backup behind.");
    }

    [TestMethod]
    public async Task Write_ReadOnlyFileInAttachments_ThenDrafts_IsAllowedBecauseContentIsIdentical()
    {
        // The full #677 sequence: read the download, fail to write it back, write drafts/ instead.
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck);
        if (!_fx.TryMakeReadOnly("attachments/deck.md"))
            Assert.Inconclusive("The test user can write read-only files (root?); permission cannot be simulated.");
        await _fx.ReadAsync("s1", "attachments/deck.md");

        Assert.IsTrue((await _fx.WriteAsync("s1", "attachments/deck.md", "# revised\n")).IsError);
        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("# revised\n", _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Edit_ReadOnlyFileInAttachments_ExplainsAndPointsAtDrafts()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);
        if (!_fx.TryMakeReadOnly("attachments/deck.md"))
            Assert.Inconclusive("The test user can write read-only files (root?); permission cannot be simulated.");

        var response = await _fx.EditAsync("s1", "attachments/deck.md", "# deck", "# revised");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!,
            "attachments/ holds downloaded files and is read-only for file tools. Edit the matching file under "
            + "drafts/ (e.g. drafts/deck.md) if one exists");
        Assert.AreEqual(Deck, _fx.ReadRaw("attachments/deck.md"));
    }

    [TestMethod]
    public async Task Write_ReadOnlyFileElsewhere_DoesNotMentionAttachments()
    {
        _fx.WriteRaw("drafts/deck.md", Deck);
        if (!_fx.TryMakeReadOnly("drafts/deck.md"))
            Assert.Inconclusive("The test user can write read-only files (root?); permission cannot be simulated.");
        await _fx.ReadAsync("s1", "drafts/deck.md");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "drafts/deck.md is not writable");
        Assert.IsFalse(response.Content!.Contains("attachments/"));
    }
}
