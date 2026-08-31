namespace RockBot.Host.Tests;

[TestClass]
public class ProfileMarkdownParserTests
{
    [TestMethod]
    public void Parse_EmptyContent_ReturnsEmptyDocument()
    {
        var doc = ProfileMarkdownParser.Parse("soul", "");

        Assert.AreEqual("soul", doc.DocumentType);
        Assert.IsNull(doc.Preamble);
        Assert.AreEqual(0, doc.Sections.Count);
        Assert.AreEqual("", doc.RawContent);
    }

    [TestMethod]
    public void Parse_NoHeadings_EntireContentBecomesPreamble()
    {
        var content = """
            # My Agent Soul

            This agent is friendly and helpful.
            It likes to tell jokes.
            """;

        var doc = ProfileMarkdownParser.Parse("soul", content);

        Assert.IsNotNull(doc.Preamble);
        Assert.IsTrue(doc.Preamble.Contains("My Agent Soul"));
        Assert.IsTrue(doc.Preamble.Contains("friendly and helpful"));
        Assert.AreEqual(0, doc.Sections.Count);
    }

    [TestMethod]
    public void Parse_SingleSection_ParsesCorrectly()
    {
        var content = """
            ## Identity

            I am a helpful coding assistant.
            """;

        var doc = ProfileMarkdownParser.Parse("soul", content);

        Assert.IsNull(doc.Preamble);
        Assert.AreEqual(1, doc.Sections.Count);
        Assert.AreEqual("Identity", doc.Sections[0].Name);
        Assert.AreEqual("I am a helpful coding assistant.", doc.Sections[0].Content);
    }

    [TestMethod]
    public void Parse_MultipleSections_ParsesAll()
    {
        var content = """
            ## Identity

            I am a helpful assistant.

            ## Personality

            Friendly and approachable.

            ## Boundaries

            Never give medical advice.
            """;

        var doc = ProfileMarkdownParser.Parse("directives", content);

        Assert.AreEqual("directives", doc.DocumentType);
        Assert.AreEqual(3, doc.Sections.Count);

        Assert.AreEqual("Identity", doc.Sections[0].Name);
        Assert.IsTrue(doc.Sections[0].Content.Contains("helpful assistant"));

        Assert.AreEqual("Personality", doc.Sections[1].Name);
        Assert.IsTrue(doc.Sections[1].Content.Contains("Friendly"));

        Assert.AreEqual("Boundaries", doc.Sections[2].Name);
        Assert.IsTrue(doc.Sections[2].Content.Contains("medical advice"));
    }

    [TestMethod]
    public void Parse_PreambleBeforeSections_BothCaptured()
    {
        var content = """
            # Echo Agent Soul

            This is the soul of the echo agent.

            ## Identity

            I am an echo agent.

            ## Worldview

            I believe in repeating things.
            """;

        var doc = ProfileMarkdownParser.Parse("soul", content);

        Assert.IsNotNull(doc.Preamble);
        Assert.IsTrue(doc.Preamble.Contains("Echo Agent Soul"));
        Assert.IsTrue(doc.Preamble.Contains("soul of the echo agent"));

        Assert.AreEqual(2, doc.Sections.Count);
        Assert.AreEqual("Identity", doc.Sections[0].Name);
        Assert.AreEqual("Worldview", doc.Sections[1].Name);
    }

    [TestMethod]
    public void Parse_PreservesRawContent()
    {
        var content = "## Identity\n\nI am a test agent.\n";

        var doc = ProfileMarkdownParser.Parse("soul", content);

        Assert.AreEqual(content, doc.RawContent);
    }

    [TestMethod]
    public void Parse_WhitespaceOnlyPreamble_TreatedAsNull()
    {
        var content = """

            ## Identity

            Test content.
            """;

        var doc = ProfileMarkdownParser.Parse("soul", content);

        Assert.IsNull(doc.Preamble);
    }

    [TestMethod]
    public void Parse_SectionWithMultipleLines_ContentPreserved()
    {
        var content = """
            ## Instructions

            1. Always be helpful.
            2. Use simple language.
            3. Be concise.

            Additional notes:
            - Keep responses short.
            """;

        var doc = ProfileMarkdownParser.Parse("directives", content);

        Assert.AreEqual(1, doc.Sections.Count);
        var section = doc.Sections[0];
        Assert.IsTrue(section.Content.Contains("1. Always be helpful."));
        Assert.IsTrue(section.Content.Contains("Keep responses short."));
    }
}
