using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RockBot.Host;

namespace RockBot.Host.Tests;

[TestClass]
public class EmbeddingTextPreparerTests
{
    private static EmbeddingTextPreparer Build(EmbeddingOptions opts) =>
        new(Options.Create(opts), NullLogger<EmbeddingTextPreparer>.Instance);

    [TestMethod]
    public void Prepare_EmptyString_ReturnsUnchanged()
    {
        var preparer = Build(new EmbeddingOptions());

        Assert.AreEqual("", preparer.Prepare(""));
    }

    [TestMethod]
    public void Prepare_ProseUnderProseCap_ReturnsUnchanged()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 100, MaxStructuredInputChars = 50 });
        var prose = new string('a', 90);

        Assert.AreEqual(prose, preparer.Prepare(prose));
    }

    [TestMethod]
    public void Prepare_ProseOverProseCap_TruncatesToProseCap()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 50, MaxStructuredInputChars = 20 });
        var prose = "Once upon a time " + new string('a', 200);

        var result = preparer.Prepare(prose);

        Assert.AreEqual(50, result.Length);
        Assert.IsTrue(result.StartsWith("Once upon a time"));
    }

    [TestMethod]
    public void Prepare_JsonObjectOverStructuredCap_TruncatesToStructuredCap()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 1000, MaxStructuredInputChars = 30 });
        // 100-char JSON object — under the prose cap, over the structured cap.
        var json = "{\"x\":\"" + new string('y', 90) + "\"}";

        var result = preparer.Prepare(json);

        Assert.AreEqual(30, result.Length, "structured cap should win for JSON-shaped input");
        Assert.IsTrue(result.StartsWith("{"));
    }

    [TestMethod]
    public void Prepare_JsonObjectWithLeadingWhitespace_StillStructured()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 1000, MaxStructuredInputChars = 20 });
        var json = "  \n\t {\"x\":\"" + new string('y', 90) + "\"}";

        var result = preparer.Prepare(json);

        Assert.AreEqual(20, result.Length);
    }

    [TestMethod]
    public void Prepare_JsonArray_IsStructured()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 1000, MaxStructuredInputChars = 25 });
        var json = "[" + string.Join(",", Enumerable.Range(0, 50).Select(i => $"\"{i}\"")) + "]";

        var result = preparer.Prepare(json);

        Assert.AreEqual(25, result.Length);
        Assert.IsTrue(result.StartsWith("["));
    }

    [TestMethod]
    public void Prepare_ProseStartingWithCurlyMidSentence_NotStructured()
    {
        // The heuristic only checks the *first* non-whitespace char. A prose paragraph
        // that mentions { somewhere isn't structured.
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 200, MaxStructuredInputChars = 30 });
        var prose = "C# initializer syntax uses { and } around object literals. " + new string('.', 200);

        var result = preparer.Prepare(prose);

        Assert.AreEqual(200, result.Length, "prose with embedded braces should still use the prose cap");
    }

    [TestMethod]
    public void Prepare_WhitespaceOnly_NotStructured_ReturnsUnchanged()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 1000, MaxStructuredInputChars = 5 });
        var ws = "     ";

        Assert.AreEqual(ws, preparer.Prepare(ws));
    }

    [TestMethod]
    public void Prepare_StructuredCapNotApplied_WhenInputFitsStructuredCap()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 1000, MaxStructuredInputChars = 100 });
        var json = "{\"x\":1}";

        Assert.AreEqual(json, preparer.Prepare(json));
    }

    [TestMethod]
    public void Prepare_LongProse_UsesProseCap()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 5000, MaxStructuredInputChars = 1000 });
        // ~2k chars of realistic English prose (lots of whitespace, mostly letters).
        var paragraph = string.Concat(Enumerable.Repeat(
            "The quick brown fox jumps over the lazy dog near the riverbank at dawn. ", 30));

        var result = preparer.Prepare(paragraph);

        Assert.AreEqual(paragraph, result, "ordinary prose should never trip the density heuristic");
    }

    [TestMethod]
    public void Prepare_LongBase64Blob_TreatedAsStructured()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 5000, MaxStructuredInputChars = 500 });
        // 2k-char base64-style blob — no whitespace, all letters/digits.
        var blob = string.Concat(Enumerable.Repeat(
            "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", 30));

        var result = preparer.Prepare(blob);

        Assert.AreEqual(500, result.Length, "no-whitespace blob should hit the structured cap");
    }

    [TestMethod]
    public void Prepare_LongUrlAndCitationMarkdown_TreatedAsStructured()
    {
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 5000, MaxStructuredInputChars = 500 });
        // Markdown evidence slice — short prose lines mixed with URLs, hashes, and dates.
        // This is the shape that was busting nomic-embed-text's 8192-token context
        // despite being under the prose cap.
        var slice = string.Concat(Enumerable.Repeat(
            "- 2026-05-15 https://example.com/posts/abc-def-ghi-jkl-mno/permalink#section-2 (sha:9f8e7d6c5b4a3) " +
            "reference 04d7ed9cf308b1a2c3d4e5f6 entry-id\n", 20));

        var result = preparer.Prepare(slice);

        Assert.AreEqual(500, result.Length, "URL/hash-heavy markdown should hit the structured cap");
    }

    [TestMethod]
    public void Prepare_DensityCheckSkipped_ForSmallInputs()
    {
        // Below ~1k chars the prose cap can't be exceeded, so density-shaped input
        // smaller than that should still take the prose path. This protects callers
        // from cap thrash on short identifier-heavy keys.
        var preparer = Build(new EmbeddingOptions { MaxInputChars = 2000, MaxStructuredInputChars = 100 });
        var smallDense = string.Concat(Enumerable.Repeat("https://example.com/abc-def ", 20));

        Assert.IsTrue(smallDense.Length < 1024);
        Assert.AreEqual(smallDense, preparer.Prepare(smallDense));
    }
}
