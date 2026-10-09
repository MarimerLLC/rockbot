using System.Globalization;
using System.Text;

namespace RockBot.Tools.FileSystem.Tests;

/// <summary>
/// file_read pages large files on whole-line boundaries instead of truncating them
/// (issue #664).
/// </summary>
[TestClass]
public class FileReadPagingTests
{
    private FileToolsFixture _fx = null!;

    [TestInitialize]
    public void Setup() => _fx = new FileToolsFixture();

    [TestCleanup]
    public void Cleanup() => _fx.Dispose();

    /// <summary>A Slidev-like deck of roughly <paramref name="targetChars"/> characters.</summary>
    internal static string Deck(int targetChars)
    {
        var sb = new StringBuilder();
        var slide = 1;
        while (sb.Length < targetChars)
        {
            sb.Append("---\n");
            sb.Append($"# Slide {slide}: MCP v2 in C#\n\n");
            sb.Append($"- point {slide}.a about transports and sessions\n");
            sb.Append($"- point {slide}.b about tool schemas and elicitation\n\n");
            slide++;
        }
        return sb.ToString();
    }

    [TestMethod]
    public async Task Read_12KbFile_ReturnsFullContentUnchanged()
    {
        var deck = Deck(12 * 1024);
        _fx.WriteRaw("drafts/deck.md", deck);

        var response = await _fx.ReadAsync("s1", "drafts/deck.md");

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual(deck, response.Content,
            "A file within the read budget must come back exactly as stored, with no header or footer.");
    }

    [TestMethod]
    public async Task Read_100KbFile_PagesOnWholeLinesWithFooters()
    {
        var content = Deck(100 * 1024);
        _fx.WriteRaw("drafts/big.md", content);
        var totalLines = LineIndex.CountLines(content);

        var (text, responses) = await _fx.ReadAllPagesAsync("s1", "drafts/big.md");

        Assert.AreEqual(content, text, "Concatenated pages must reproduce the file exactly.");
        Assert.IsTrue(responses.Count >= 2, "A 100 KB file must not fit one 64,000-char page.");

        var first = responses[0];
        StringAssert.StartsWith(first, "[file_read drafts/big.md: lines 1–");
        var (firstPage, next) = FileToolsFixture.ParsePage(first);
        Assert.IsNotNull(next);
        Assert.IsTrue(firstPage.Length <= _fx.Options.FileReadMaxChars);
        Assert.IsTrue(firstPage.EndsWith('\n'), "A page must end on a whole line.");
        var inv = CultureInfo.InvariantCulture;
        StringAssert.Contains(first,
            $"[lines 1–{next - 1} of {totalLines} ({firstPage.Length.ToString("N0", inv)} of "
            + $"{content.Length.ToString("N0", inv)} chars) — call file_read with offset={next} to continue");

        StringAssert.Contains(responses[^1], "— end of file]");
        StringAssert.Contains(responses[^1], $"of {totalLines} (");
    }

    [TestMethod]
    public async Task Read_WithOffsetAndLimit_ReturnsThoseLinesWithFooter()
    {
        _fx.WriteRaw("notes.md", "one\ntwo\nthree\nfour\nfive\n");

        var response = await _fx.ReadAsync("s1", "notes.md", offset: 2, limit: 2);

        Assert.IsFalse(response.IsError, response.Content);
        Assert.AreEqual(
            "[file_read notes.md: lines 2–3 of 5]\ntwo\nthree\n"
            + "[lines 2–3 of 5 (10 of 24 chars) — call file_read with offset=4 to continue.]",
            response.Content);
    }

    [TestMethod]
    public async Task Read_LastPage_SaysEndOfFile()
    {
        _fx.WriteRaw("notes.md", "one\ntwo\nthree");

        var response = await _fx.ReadAsync("s1", "notes.md", offset: 3);

        Assert.AreEqual(
            "[file_read notes.md: lines 3–3 of 3]\nthree\n[lines 3–3 of 3 (5 of 13 chars) — end of file]",
            response.Content);
    }

    [TestMethod]
    public async Task Read_OffsetPastEnd_IsAnError()
    {
        _fx.WriteRaw("notes.md", "one\ntwo\n");

        var response = await _fx.ReadAsync("s1", "notes.md", offset: 10);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "has 2 lines");
    }

    [TestMethod]
    public async Task Read_InvalidOffset_IsAnError()
    {
        _fx.WriteRaw("notes.md", "one\n");

        var response = await _fx.ReadAsync("s1", "notes.md", offset: 0);

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "offset must be a whole number");
    }

    [TestMethod]
    public async Task Read_LineLongerThanBudget_ShowsPartialLineAndContinues()
    {
        using var fx = new FileToolsFixture(readMaxChars: 10);
        fx.WriteRaw("wide.txt", new string('x', 25) + "\nshort\n");

        var response = await fx.ReadAsync("s1", "wide.txt");

        Assert.IsFalse(response.IsError);
        StringAssert.Contains(response.Content!, "line 1 is 26 chars");
        StringAssert.Contains(response.Content!, "offset=2");
    }

    [TestMethod]
    public async Task Read_MissingFile_SuggestsSameNameElsewhere()
    {
        _fx.WriteRaw("drafts/mcp-v2-deck.md", "x");

        var response = await _fx.ReadAsync("s1", "Talks/mcp-v2-deck.md");

        Assert.IsTrue(response.IsError);
        StringAssert.Contains(response.Content!, "drafts/mcp-v2-deck.md");
    }
}
