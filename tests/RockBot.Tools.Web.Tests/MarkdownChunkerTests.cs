namespace RockBot.Tools.Web.Tests;

[TestClass]
public class MarkdownChunkerTests
{
    [TestMethod]
    public void Chunk_ShortContent_ReturnsSingleChunk()
    {
        var markdown = "Hello, world!";

        var chunks = MarkdownChunker.Chunk(markdown, maxLength: 1000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual("Hello, world!", chunks[0].Content);
    }

    [TestMethod]
    public void Chunk_SplitsAtH1Headings()
    {
        var markdown = """
            # First Section
            Content of first section.

            # Second Section
            Content of second section.
            """;

        var chunks = MarkdownChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(2, chunks.Count);
        Assert.AreEqual("First Section", chunks[0].Heading);
        StringAssert.Contains(chunks[0].Content, "Content of first section.");
        Assert.AreEqual("Second Section", chunks[1].Heading);
        StringAssert.Contains(chunks[1].Content, "Content of second section.");
    }

    [TestMethod]
    public void Chunk_SplitsAtH2Headings()
    {
        var markdown = """
            ## Overview
            Some overview text.

            ## Details
            Some detail text.
            """;

        var chunks = MarkdownChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(2, chunks.Count);
        Assert.AreEqual("Overview", chunks[0].Heading);
        Assert.AreEqual("Details", chunks[1].Heading);
    }

    [TestMethod]
    public void Chunk_SplitsAtH3Headings()
    {
        var markdown = """
            ### Sub A
            Content A.

            ### Sub B
            Content B.
            """;

        var chunks = MarkdownChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(2, chunks.Count);
        Assert.AreEqual("Sub A", chunks[0].Heading);
        Assert.AreEqual("Sub B", chunks[1].Heading);
    }

    [TestMethod]
    public void Chunk_OversizedSection_SplitsAtBlankLines()
    {
        // Two paragraphs, each 300 chars — max 400
        var para1 = new string('a', 300);
        var para2 = new string('b', 300);
        var markdown = $"# Big Section\n{para1}\n\n{para2}";

        var chunks = MarkdownChunker.Chunk(markdown, maxLength: 400);

        // Should be split into at least 2 chunks
        Assert.IsTrue(chunks.Count >= 2, $"Expected >= 2 chunks, got {chunks.Count}");
        Assert.IsTrue(chunks.All(c => c.Content.Length <= 400),
            "Each chunk should not exceed maxLength");
    }

    [TestMethod]
    public void Chunk_PathologicalContent_HardSplits()
    {
        // No headings, no blank lines — must hard-split
        var content = new string('x', 1000);

        var chunks = MarkdownChunker.Chunk(content, maxLength: 300);

        Assert.IsTrue(chunks.Count >= 4, $"Expected >= 4 chunks, got {chunks.Count}");
        Assert.IsTrue(chunks.All(c => c.Content.Length <= 300),
            "Each chunk should not exceed maxLength");

        // All content should be preserved
        var combined = string.Concat(chunks.Select(c => c.Content));
        Assert.AreEqual(content, combined);
    }

    [TestMethod]
    public void Chunk_HeadingUsedAsChunkTitle()
    {
        var markdown = """
            # My Great Heading
            Body text here.
            """;

        var chunks = MarkdownChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual("My Great Heading", chunks[0].Heading);
    }

    [TestMethod]
    public void Chunk_EmptyInput_ReturnsSingleEmptyChunk()
    {
        var chunks = MarkdownChunker.Chunk(string.Empty, maxLength: 1000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(string.Empty, chunks[0].Content);
    }
}
