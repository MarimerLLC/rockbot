using Microsoft.Extensions.Logging;

namespace RockBot.Tools.FileSystem.Tests;

/// <summary>
/// Issue #677: a full read of identical content under another path counts as having read
/// the target, and refusals steer the model away from renamed sibling copies.
/// </summary>
[TestClass]
public class FileWriteIdenticalContentTests
{
    private const string Deck = "# MCP v2 in C#\n\n---\n\nslide two\n";

    private FileToolsFixture _fx = null!;

    [TestInitialize]
    public void Setup() => _fx = new FileToolsFixture();

    [TestCleanup]
    public void Cleanup() => _fx.Dispose();

    [TestMethod]
    public async Task Write_AfterFullReadOfIdenticalFileElsewhere_IsAllowed()
    {
        // The #677 shape: the OneDrive download in attachments/ is the same as the drafts/ copy.
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck);
        Assert.IsFalse((await _fx.ReadAsync("s1", "attachments/deck.md")).IsError);

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual("# revised\n", _fx.ReadRaw("drafts/deck.md"));
        Assert.AreEqual(Deck, _fx.ReadRaw(".prev/drafts/deck.md"), "The replaced version is still backed up.");
        Assert.IsTrue(
            _fx.Log.At(LogLevel.Information).Any(m =>
                m.Contains("Overwrite of drafts/deck.md allowed") && m.Contains("attachments/deck.md")),
            string.Join("\n", _fx.Log.At(LogLevel.Information)));
    }

    [TestMethod]
    public async Task Write_AfterFullPagedReadOfIdenticalFileElsewhere_IsAllowed()
    {
        using var fx = new FileToolsFixture(readMaxChars: 30);
        var content = string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i:00}\n"));
        fx.WriteRaw("attachments/deck.md", content);
        fx.WriteRaw("drafts/deck.md", content);
        await fx.ReadAllPagesAsync("s1", "attachments/deck.md");

        var response = await fx.WriteAsync("s1", "drafts/deck.md", "replacement\n");

        Assert.IsFalse(response.IsError, response.Content);
    }

    [TestMethod]
    public async Task Write_ContentDiffersFromEverythingRead_IsRefused()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck + "a slide only the drafts copy has\n");
        await _fx.ReadAsync("s1", "attachments/deck.md");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "Refusing to overwrite drafts/deck.md");
        Assert.AreEqual(Deck + "a slide only the drafts copy has\n", _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Write_TargetChangedAfterIdenticalRead_IsRefused()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck);
        await _fx.ReadAsync("s1", "attachments/deck.md");

        // Something else changes the target after the session saw its (then identical) twin.
        _fx.WriteRaw("drafts/deck.md", Deck + "someone else's slide\n");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsTrue(response.IsError, "The session has not seen the target's current content anywhere.");
        Assert.AreEqual(Deck + "someone else's slide\n", _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Write_TwinChangedAfterItWasRead_IsStillAllowed()
    {
        // What counts is that the session saw these exact bytes, not where they live now.
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck);
        await _fx.ReadAsync("s1", "attachments/deck.md");
        _fx.WriteRaw("attachments/deck.md", "a newer download\n");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsFalse(response.IsError, response.Content);
    }

    [TestMethod]
    public async Task Write_IdenticalFileReadOnlyPartly_IsRefused()
    {
        using var fx = new FileToolsFixture(readMaxChars: 30);
        var content = string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i:00}\n"));
        fx.WriteRaw("attachments/deck.md", content);
        fx.WriteRaw("drafts/deck.md", content);
        await fx.ReadAsync("s1", "attachments/deck.md");

        var response = await fx.WriteAsync("s1", "drafts/deck.md", "replacement\n");

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task Write_IdenticalFileReadByAnotherSession_IsRefused()
    {
        _fx.WriteRaw("attachments/deck.md", Deck);
        _fx.WriteRaw("drafts/deck.md", Deck);
        await _fx.ReadAsync("subagent/task-1", "attachments/deck.md");

        var response = await _fx.WriteAsync("session/primary", "drafts/deck.md", "# revised\n");

        Assert.IsTrue(response.IsError);
    }

    [TestMethod]
    public async Task Refusal_NeverRead_TellsModelNotToWriteARenamedCopy()
    {
        _fx.WriteRaw("drafts/deck.md", Deck);

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "Read it first (one file_read call) or use file_edit");
        StringAssert.Contains(response.Content!,
            "Do not write a renamed copy instead — the original would go stale and later turns would edit the "
            + "wrong file.");
    }

    [TestMethod]
    public async Task Refusal_Stale_TellsModelNotToWriteARenamedCopy()
    {
        _fx.WriteRaw("drafts/deck.md", Deck);
        await _fx.ReadAsync("s1", "drafts/deck.md");
        _fx.WriteRaw("drafts/deck.md", "changed\n");

        var response = await _fx.WriteAsync("s1", "drafts/deck.md", "# revised\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "(one file_read call) or use file_edit");
        StringAssert.Contains(response.Content!, "Do not write a renamed copy instead");
    }

    [TestMethod]
    public async Task Refusal_Partial_TellsModelNotToWriteARenamedCopy()
    {
        using var fx = new FileToolsFixture(readMaxChars: 30);
        fx.WriteRaw("drafts/deck.md", string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i:00}\n")));
        await fx.ReadAsync("s1", "drafts/deck.md");

        var response = await fx.WriteAsync("s1", "drafts/deck.md", "reconstruction\n");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "Do not write a renamed copy instead");
    }
}
