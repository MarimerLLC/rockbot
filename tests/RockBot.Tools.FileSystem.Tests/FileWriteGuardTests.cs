namespace RockBot.Tools.FileSystem.Tests;

/// <summary>
/// file_write refuses to overwrite an existing file unless the calling session has seen its
/// current version in full (issue #664).
/// </summary>
[TestClass]
public class FileWriteGuardTests
{
    private FileToolsFixture _fx = null!;

    [TestInitialize]
    public void Setup() => _fx = new FileToolsFixture();

    [TestCleanup]
    public void Cleanup() => _fx.Dispose();

    [TestMethod]
    public async Task Write_NewFile_IsAllowed()
    {
        var response = await _fx.WriteAsync("s1", "drafts/new.md", "hello\n");

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("hello\n", _fx.ReadRaw("drafts/new.md"));
        Assert.IsFalse(response.Content!.Contains(".prev/"), "A new file has no previous version to keep.");
    }

    [TestMethod]
    public async Task Write_ExistingFileNeverRead_IsRefused()
    {
        _fx.WriteRaw("drafts/deck.md", "original\n");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "Refusing to overwrite drafts/deck.md");
        StringAssert.Contains(response.Content!, "has not read it");
        StringAssert.Contains(response.Content!, "file_edit");
        Assert.AreEqual("original\n", _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Write_AfterPartialPagedRead_IsRefusedWithNextOffset()
    {
        using var fx = new FileToolsFixture(readMaxChars: 30);
        var content = string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i:00}\n"));
        fx.WriteRaw("drafts/deck.md", content);

        var firstPage = await fx.ReadAsync("s1", "drafts/deck.md");
        var (_, next) = FileToolsFixture.ParsePage(firstPage.Content!);
        Assert.IsNotNull(next);

        var response = await fx.WriteAsync("s1", "drafts/deck.md", "reconstruction\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!,
            $"Refusing to overwrite drafts/deck.md: this session has not read the current version in full "
            + $"(you have seen lines 1–{next - 1} of 40). Read the rest with file_read offset={next}, "
            + "or use file_edit for targeted changes.");
        Assert.AreEqual(content, fx.ReadRaw("drafts/deck.md"), "A refused write must not touch the file.");
    }

    [TestMethod]
    public async Task Write_AfterFullPagedCoverage_IsAllowed()
    {
        using var fx = new FileToolsFixture(readMaxChars: 30);
        var content = string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i:00}\n"));
        fx.WriteRaw("drafts/deck.md", content);

        var (text, responses) = await fx.ReadAllPagesAsync("s1", "drafts/deck.md");
        Assert.AreEqual(content, text);
        Assert.IsTrue(responses.Count > 1);

        var response = await fx.WriteAsync("s1", "drafts/deck.md", "replacement\n");

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("replacement\n", fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Write_AfterFullRead_IsAllowed()
    {
        _fx.WriteRaw("drafts/deck.md", "original\n");
        await _fx.ReadAsync("s1", "drafts/deck.md");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        Assert.IsFalse(response.IsError, response.Content);
    }

    [TestMethod]
    public async Task Write_AfterOwnWrite_IsAllowed()
    {
        Assert.IsFalse((await _fx.WriteAsync("s1", "drafts/deck.md", "v1\n")).IsError);

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "v2\n");

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("v2\n", _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Write_FileChangedSinceRead_IsRefused()
    {
        _fx.WriteRaw("drafts/deck.md", "original\n");
        await _fx.ReadAsync("s1", "drafts/deck.md");

        // A script pod, another session, or a person changes the file afterwards.
        _fx.WriteRaw("drafts/deck.md", "someone else's update\n");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "has changed since this session last read or wrote it");
        Assert.AreEqual("someone else's update\n", _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Write_OtherSessionWroteFile_IsRefused()
    {
        // The #664 shape: a subagent (its own session) writes the deck; the primary has not
        // read it, so it may not overwrite it.
        Assert.IsFalse((await _fx.WriteAsync("subagent/task-1", "drafts/deck.md", "the real deck\n")).IsError);

        var response = await _fx.WriteAsync("session/primary", "drafts/deck.md", "a reconstruction\n");

        Assert.IsTrue(response.IsError);
        Assert.AreEqual("the real deck\n", _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Write_AfterEditWithoutFullRead_IsStillRefused()
    {
        _fx.WriteRaw("drafts/deck.md", "alpha\nbeta\ngamma\n");

        Assert.IsFalse((await _fx.EditAsync("s1", "drafts/deck.md", "beta", "BETA")).IsError);
        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        Assert.IsTrue(response.IsError,
            "An edit proves the session knew old_string, not the rest of the file.");
    }

    [TestMethod]
    public async Task Write_AfterFullReadThenEdit_IsAllowed()
    {
        _fx.WriteRaw("drafts/deck.md", "alpha\nbeta\ngamma\n");
        await _fx.ReadAsync("s1", "drafts/deck.md");
        Assert.IsFalse((await _fx.EditAsync("s1", "drafts/deck.md", "beta", "BETA")).IsError);

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        Assert.IsFalse(response.IsError, response.Content);
    }

    [TestMethod]
    public async Task Overwrite_KeepsPreviousVersionUnderPrev()
    {
        _fx.WriteRaw("drafts/deck.md", "original\n");
        await _fx.ReadAsync("s1", "drafts/deck.md");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content!, "previous version is kept at .prev/drafts/deck.md");
        Assert.AreEqual("original\n", _fx.ReadRaw(".prev/drafts/deck.md"));
    }

    [TestMethod]
    public async Task Edit_KeepsPreviousVersionUnderPrev()
    {
        _fx.WriteRaw("canon/NPCs.md", "Georgie, neutral\n");

        var response = await _fx.EditAsync("s1", "canon/NPCs.md", "neutral", "owes a favour");

        Assert.IsFalse(response.IsError, response.Content);
        StringAssert.Contains(response.Content!, ".prev/canon/NPCs.md");
        Assert.AreEqual("Georgie, neutral\n", _fx.ReadRaw(".prev/canon/NPCs.md"));
        Assert.AreEqual("Georgie, owes a favour\n", _fx.ReadRaw("canon/NPCs.md"));
    }

    [TestMethod]
    public async Task List_HidesBackupsUnlessAskedByPrefix()
    {
        _fx.WriteRaw("drafts/deck.md", "original\n");
        await _fx.ReadAsync("s1", "drafts/deck.md");
        await _fx.WriteAsync("s1", "drafts/deck.md", "rewrite\n");

        var all = await _fx.ListAsync();
        var backups = await _fx.ListAsync(".prev/");

        Assert.AreEqual("[\"drafts/deck.md\"]", all.Content);
        Assert.AreEqual("[\".prev/drafts/deck.md\"]", backups.Content);
    }
}
