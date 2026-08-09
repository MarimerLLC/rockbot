using RockBot.Host;

namespace RockBot.Tools.FileSystem.Tests;

[TestClass]
public class TextEditTests
{
    [TestMethod]
    public void Apply_ReplacesSingleOccurrence()
    {
        var result = TextEdit.Apply("the quick brown fox", "brown", "red");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("the quick red fox", result.Content);
        Assert.AreEqual(1, result.ReplacementCount);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void Apply_LeavesRestOfContentByteForByte()
    {
        var original = "# Title\n\nPara one.\n\n- item a\n- item b\n\n## Sub\n\ttabbed  spaced\n";

        var result = TextEdit.Apply(original, "item a", "item A");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(original.Replace("item a", "item A"), result.Content);
    }

    [TestMethod]
    public void Apply_ReturnsNotFound_WhenOldTextAbsent()
    {
        var result = TextEdit.Apply("hello world", "goodbye", "farewell");

        Assert.AreEqual(TextEditStatus.NotFound, result.Status);
        Assert.IsNull(result.Content);
        Assert.AreEqual(0, result.ReplacementCount);
        StringAssert.Contains(result.Error!, "not found");
    }

    [TestMethod]
    public void Apply_ReturnsAmbiguous_WhenMultipleMatchesAndNotReplaceAll()
    {
        var result = TextEdit.Apply("cat dog cat", "cat", "bird");

        Assert.AreEqual(TextEditStatus.Ambiguous, result.Status);
        Assert.IsNull(result.Content);
        StringAssert.Contains(result.Error!, "2 times");
    }

    [TestMethod]
    public void Apply_ReplacesEveryOccurrence_WhenReplaceAll()
    {
        var result = TextEdit.Apply("cat dog cat", "cat", "bird", replaceAll: true);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("bird dog bird", result.Content);
        Assert.AreEqual(2, result.ReplacementCount);
    }

    [TestMethod]
    public void Apply_ReturnsEmptyOldText_WhenOldTextIsEmpty()
    {
        var result = TextEdit.Apply("content", "", "new");

        Assert.AreEqual(TextEditStatus.EmptyOldText, result.Status);
        Assert.IsNull(result.Content);
    }

    [TestMethod]
    public void Apply_ReturnsNoChange_WhenOldAndNewAreIdentical()
    {
        var result = TextEdit.Apply("content here", "here", "here");

        Assert.AreEqual(TextEditStatus.NoChange, result.Status);
        Assert.IsNull(result.Content);
    }

    [TestMethod]
    public void Apply_DeletesText_WhenNewTextIsEmpty()
    {
        var result = TextEdit.Apply("keep this, drop that", ", drop that", "");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("keep this", result.Content);
    }

    [TestMethod]
    public void Apply_IsCaseSensitive()
    {
        var result = TextEdit.Apply("Hello World", "hello", "goodbye");

        Assert.AreEqual(TextEditStatus.NotFound, result.Status);
    }

    [TestMethod]
    public void Apply_CountsNonOverlappingOccurrences()
    {
        // "aaaa" contains "aa" twice non-overlapping, not three times overlapping.
        var result = TextEdit.Apply("aaaa", "aa", "b", replaceAll: true);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("bb", result.Content);
        Assert.AreEqual(2, result.ReplacementCount);
    }

    [TestMethod]
    public void Apply_MatchesAcrossLines()
    {
        var original = "line one\nline two\nline three\n";

        var result = TextEdit.Apply(original, "line one\nline two", "line 1\nline 2");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("line 1\nline 2\nline three\n", result.Content);
    }

    [TestMethod]
    public void Apply_DisambiguatesWithSurroundingContext()
    {
        var original = "## Alice\nstatus: active\n\n## Bob\nstatus: active\n";

        // "status: active" alone is ambiguous...
        var ambiguous = TextEdit.Apply(original, "status: active", "status: retired");
        Assert.AreEqual(TextEditStatus.Ambiguous, ambiguous.Status);

        // ...but including the heading makes it unique.
        var result = TextEdit.Apply(original, "## Bob\nstatus: active", "## Bob\nstatus: retired");
        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("## Alice\nstatus: active\n\n## Bob\nstatus: retired\n", result.Content);
    }

    [TestMethod]
    public void Apply_MatchesLfOldTextAgainstCrLfContent()
    {
        var original = "line one\r\nline two\r\nline three\r\n";

        var result = TextEdit.Apply(original, "line one\nline two", "line 1\nline 2");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("line 1\r\nline 2\r\nline three\r\n", result.Content);
    }

    [TestMethod]
    public void Apply_PrefersExactMatch_OverCrLfFallback()
    {
        // Content has a bare-LF region and a CRLF region. An LF oldText must hit the
        // literal LF match rather than being converted and hitting the CRLF one.
        var original = "alpha\nbeta\r\nalpha\r\nbeta\r\n";

        var result = TextEdit.Apply(original, "alpha\nbeta", "X");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("X\r\nalpha\r\nbeta\r\n", result.Content);
        Assert.AreEqual(1, result.ReplacementCount);
    }

    [TestMethod]
    public void Apply_MatchesCrLfOldTextAgainstLfContent()
    {
        // The reverse of the LF-against-CRLF case: a caller reading through a
        // CRLF-normalizing source editing a Unix-authored document.
        var original = "line one\nline two\nline three\n";

        var result = TextEdit.Apply(original, "line one\r\nline two", "line 1\r\nline 2");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("line 1\nline 2\nline three\n", result.Content);
    }

    [TestMethod]
    public void Apply_ConvertsNewTextToCrLf_OnExactMatchPath()
    {
        // oldText matches without conversion, so only newText carries foreign endings.
        // Inserting it verbatim would leave the document with mixed line endings.
        var original = "alpha\r\nbeta\r\n";

        var result = TextEdit.Apply(original, "beta", "beta\nand gamma");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("alpha\r\nbeta\r\nand gamma\r\n", result.Content);
        Assert.IsFalse(HasBareLf(result.Content!), "a CRLF document must stay CRLF");
    }

    [TestMethod]
    public void Apply_ConvertsNewTextToLf_OnExactMatchPath()
    {
        var original = "alpha\nbeta\n";

        var result = TextEdit.Apply(original, "beta", "beta\r\nand gamma");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("alpha\nbeta\nand gamma\n", result.Content);
    }

    [TestMethod]
    public void Apply_LeavesNewTextAlone_WhenContentHasMixedEndings()
    {
        // Mixed content has no single style to conform to, so imposing one would
        // rewrite line endings the caller never asked to touch.
        var original = "alpha\r\nbeta\ngamma\r\n";

        var result = TextEdit.Apply(original, "gamma", "gamma\ndelta");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("alpha\r\nbeta\ngamma\ndelta\r\n", result.Content);
    }

    [TestMethod]
    public void Apply_ReturnsNoChange_WhenTextsDifferOnlyInLineEndings()
    {
        var result = TextEdit.Apply("alpha\r\nbeta\r\n", "alpha\r\nbeta", "alpha\nbeta");

        Assert.AreEqual(TextEditStatus.NoChange, result.Status);
        Assert.IsNull(result.Content);
        StringAssert.Contains(result.Error!, "line endings");
    }

    private static bool HasBareLf(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\n' && (i == 0 || value[i - 1] != '\r'))
                return true;
        }

        return false;
    }

    [TestMethod]
    public void Apply_DoesNotAppendOrStripTrailingNewline()
    {
        var withNewline = TextEdit.Apply("body\n", "body", "text");
        Assert.AreEqual("text\n", withNewline.Content);

        var withoutNewline = TextEdit.Apply("body", "body", "text");
        Assert.AreEqual("text", withoutNewline.Content);
    }

    [TestMethod]
    public void Apply_PreservesUnicodeContent()
    {
        var original = "Turkana — long-lived\nNarvik — predatory\n";

        var result = TextEdit.Apply(original, "Narvik — predatory", "Narvik — fast, predatory");

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual("Turkana — long-lived\nNarvik — fast, predatory\n", result.Content);
    }
}
