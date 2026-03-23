using RockBot.Host;

namespace RockBot.Tools.Web.Tests;

[TestClass]
public class ContentChunkerTests
{
    [TestMethod]
    public void Chunk_ShortContent_ReturnsSingleChunk()
    {
        var markdown = "Hello, world!";

        var chunks = ContentChunker.Chunk(markdown, maxLength: 1000);

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

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

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

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

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

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

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

        var chunks = ContentChunker.Chunk(markdown, maxLength: 400);

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

        var chunks = ContentChunker.Chunk(content, maxLength: 300);

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

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual("My Great Heading", chunks[0].Heading);
    }

    [TestMethod]
    public void Chunk_EmptyInput_ReturnsSingleEmptyChunk()
    {
        var chunks = ContentChunker.Chunk(string.Empty, maxLength: 1000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(string.Empty, chunks[0].Content);
    }

    [TestMethod]
    public void Chunk_TracksHeadingLevel_H1()
    {
        var markdown = """
            # Top Level
            Content here.
            """;

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(1, chunks[0].HeadingLevel);
    }

    [TestMethod]
    public void Chunk_TracksHeadingLevel_H2()
    {
        var markdown = """
            ## Sub Level
            Content here.
            """;

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(2, chunks[0].HeadingLevel);
    }

    [TestMethod]
    public void Chunk_TracksHeadingLevel_H3()
    {
        var markdown = """
            ### Detail Level
            Content here.
            """;

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(3, chunks[0].HeadingLevel);
    }

    [TestMethod]
    public void Chunk_NoHeading_HeadingLevelIsZero()
    {
        var chunks = ContentChunker.Chunk("Just plain text", maxLength: 10_000);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(0, chunks[0].HeadingLevel);
    }

    [TestMethod]
    public void Chunk_MixedHeadingLevels_PreservesEachLevel()
    {
        var markdown = """
            # Top
            Top content.

            ## Middle
            Middle content.

            ### Bottom
            Bottom content.
            """;

        var chunks = ContentChunker.Chunk(markdown, maxLength: 10_000);

        Assert.AreEqual(3, chunks.Count);
        Assert.AreEqual(1, chunks[0].HeadingLevel);
        Assert.AreEqual(2, chunks[1].HeadingLevel);
        Assert.AreEqual(3, chunks[2].HeadingLevel);
    }

    [TestMethod]
    public void BuildOutline_ProducesHierarchicalMarkdown()
    {
        var chunks = new List<ChunkInfo>
        {
            new("Getting Started", "...", 1),
            new("Installation", "...", 2),
            new("Configuration", "...", 2),
            new("Environment Variables", "...", 3),
            new("API Reference", "...", 1),
        };
        var keys = new List<string>
        {
            "session/s1/web-example-chunk0",
            "session/s1/web-example-chunk1",
            "session/s1/web-example-chunk2",
            "session/s1/web-example-chunk3",
            "session/s1/web-example-chunk4",
        };

        var outline = ContentChunker.BuildOutline(chunks, keys);

        StringAssert.Contains(outline, "## Document Outline");
        // H1 items should not be indented
        StringAssert.Contains(outline, "- **Getting Started** → `session/s1/web-example-chunk0`");
        StringAssert.Contains(outline, "- **API Reference** → `session/s1/web-example-chunk4`");
        // H2 items should be indented 2 spaces
        StringAssert.Contains(outline, "  - **Installation** → `session/s1/web-example-chunk1`");
        StringAssert.Contains(outline, "  - **Configuration** → `session/s1/web-example-chunk2`");
        // H3 items should be indented 4 spaces
        StringAssert.Contains(outline, "    - **Environment Variables** → `session/s1/web-example-chunk3`");
    }

    [TestMethod]
    public void BuildOutline_EmptyChunks_ReturnsEmpty()
    {
        var outline = ContentChunker.BuildOutline([], []);

        Assert.AreEqual(string.Empty, outline);
    }

    [TestMethod]
    public void BuildOutline_NoHeadings_UsesFallbackLabels()
    {
        var chunks = new List<ChunkInfo>
        {
            new("", "content a", 0),
            new("", "content b", 0),
        };
        var keys = new List<string> { "key0", "key1" };

        var outline = ContentChunker.BuildOutline(chunks, keys);

        StringAssert.Contains(outline, "- **Part 0** → `key0`");
        StringAssert.Contains(outline, "- **Part 1** → `key1`");
    }
}
