namespace RockBot.Tools.FileSystem.Tests;

/// <summary>
/// file_edit failures carry enough of the file for the model to recover without falling
/// back to a full rewrite (issue #664).
/// </summary>
[TestClass]
public class FileEditRecoveryTests
{
    private FileToolsFixture _fx = null!;

    [TestInitialize]
    public void Setup() => _fx = new FileToolsFixture();

    [TestCleanup]
    public void Cleanup() => _fx.Dispose();

    [TestMethod]
    public async Task Edit_NotFound_ShowsNearestRegionWithLineNumbers()
    {
        var content = string.Join("\n",
            "# Deck",
            "",
            "---",
            "## Transports",
            "- stdio and Streamable HTTP are both supported",
            "- SSE is deprecated",
            "---",
            "## Tools",
            "- schemas are JSON Schema 2020-12",
            "") ;
        _fx.WriteRaw("drafts/deck.md", content);

        // Paraphrased from a summary, not copied from the file.
        var response = await _fx.EditAsync("s1", "drafts/deck.md",
            "- stdio and streamable HTTP are supported", "- stdio only");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "oldText was not found");
        StringAssert.Contains(response.Content!, "closest match is near line 5");
        StringAssert.Contains(response.Content!, "5| - stdio and Streamable HTTP are both supported");
        StringAssert.Contains(response.Content!, "4| ## Transports");
        Assert.AreEqual(content, _fx.ReadRaw("drafts/deck.md"));
    }

    [TestMethod]
    public async Task Edit_NotFound_FirstLineMatchesButRestDiffers_SaysSo()
    {
        _fx.WriteRaw("notes.md", "alpha\n  beta\ngamma\n");

        var response = await _fx.EditAsync("s1", "notes.md", "beta\ndelta", "x");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "Line 2 of the file matches line 1 of old_string");
        StringAssert.Contains(response.Content!, "2|   beta");
    }

    [TestMethod]
    public async Task Edit_NotFound_NothingSimilar_SaysTextIsNotFromThisFile()
    {
        _fx.WriteRaw("notes.md", "alpha\nbeta\n");

        var response = await _fx.EditAsync("s1", "notes.md", "Quarterly revenue grew by twelve percent", "x");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "No line of the file resembles old_string");
    }

    [TestMethod]
    public async Task Edit_MissingPath_SuggestsSameNameFileInDrafts_AndDoesNotPushFileWrite()
    {
        _fx.WriteRaw("drafts/mcp-v2-csharp-slidev-review.md", "deck\n");

        var response = await _fx.EditAsync("s1", "Talks/mcp-v2-csharp-slidev-review.md", "deck", "Deck");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "File not found on the shared volume: Talks/mcp-v2-csharp-slidev-review.md");
        StringAssert.Contains(response.Content!, "drafts/mcp-v2-csharp-slidev-review.md");
        StringAssert.Contains(response.Content!, "OneDrive");
        Assert.IsFalse(response.Content!.Contains("Use file_write to create it", StringComparison.Ordinal),
            "The old hint invited the model to create a second copy or overwrite the wrong file.");
        Assert.IsFalse(File.Exists(_fx.FullPath("Talks/mcp-v2-csharp-slidev-review.md")));
    }

    [TestMethod]
    public async Task Edit_MissingPath_NoSimilarFiles_StillExplainsRemotePaths()
    {
        var response = await _fx.EditAsync("s1", "Talks/nothing-like-it.md", "a", "b");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "not files on the shared volume");
        Assert.IsFalse(response.Content!.Contains("Files with the same name", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Edit_MissingPath_IgnoresBackups()
    {
        _fx.WriteRaw(".prev/drafts/deck.md", "old\n");

        var response = await _fx.EditAsync("s1", "Talks/deck.md", "a", "b");

        Assert.IsFalse(response.Content!.Contains(".prev/", StringComparison.Ordinal),
            "A backup is not a file the model should be editing.");
    }

    [TestMethod]
    public async Task LargeFile_ReadInPages_ThenEditMiddleLine()
    {
        // Issue #664 acceptance: write 100 KB, read it in pages, edit a line in the middle.
        var lines = Enumerable.Range(1, 2500).Select(i => $"row {i:0000}: {new string('z', 30)}").ToList();
        var content = string.Join("\n", lines) + "\n";
        Assert.IsTrue(content.Length >= 100 * 1024);

        Assert.IsFalse((await _fx.WriteAsync("s1", "drafts/big.md", content)).IsError);
        var (text, responses) = await _fx.ReadAllPagesAsync("s1", "drafts/big.md");
        Assert.AreEqual(content, text);
        Assert.IsTrue(responses.Count >= 2);

        var target = lines[1249];
        var response = await _fx.EditAsync("s1", "drafts/big.md", target, "row 1250: edited");

        Assert.IsFalse(response.IsError, response.Content);
        var edited = _fx.ReadRaw("drafts/big.md");
        StringAssert.Contains(edited, "row 1249: " + new string('z', 30) + "\nrow 1250: edited\nrow 1251: ");
        Assert.AreEqual(content, _fx.ReadRaw(".prev/drafts/big.md"));
    }
}
