using RockBot.UserProxy.Rendering;

namespace RockBot.UserProxy.Tests;

[TestClass]
public sealed class HtmlPlainTextRendererTests
{
    [TestMethod]
    public void PlainText_PassesThroughUnchanged()
    {
        Assert.AreEqual("hello world", HtmlPlainTextRenderer.StripHtml("hello world"));
    }

    [TestMethod]
    public void ColorSpan_InnerTextRetained()
    {
        Assert.AreEqual(
            "danger",
            HtmlPlainTextRenderer.StripHtml("<span style=\"color:red\">danger</span>"));
    }

    [TestMethod]
    public void StrongTag_InnerTextRetained()
    {
        Assert.AreEqual("x", HtmlPlainTextRenderer.StripHtml("<strong>x</strong>"));
    }

    [TestMethod]
    public void Svg_ReplacedWithPlaceholder()
    {
        var input = "before <svg width=\"10\" height=\"10\"><rect x=\"0\"/></svg> after";
        Assert.AreEqual(
            "before [chart — view in Blazor] after",
            HtmlPlainTextRenderer.StripHtml(input));
    }

    [TestMethod]
    public void Svg_MultilineInner_ReplacedWithPlaceholder()
    {
        var input = "<svg>\n  <rect />\n  <circle />\n</svg>";
        Assert.AreEqual("[chart — view in Blazor]", HtmlPlainTextRenderer.StripHtml(input));
    }

    [TestMethod]
    public void MultilineContent_PreservesNewlines()
    {
        var input = "line1\n<strong>line2</strong>\nline3";
        Assert.AreEqual("line1\nline2\nline3", HtmlPlainTextRenderer.StripHtml(input));
    }

    [TestMethod]
    public void MultipleTags_AllStripped()
    {
        var input = "<p>hello <b>bold</b> and <em>italic</em></p>";
        Assert.AreEqual("hello bold and italic", HtmlPlainTextRenderer.StripHtml(input));
    }

    [TestMethod]
    public void NullInput_ReturnsEmptyString()
    {
        Assert.AreEqual(string.Empty, HtmlPlainTextRenderer.StripHtml(null));
    }

    [TestMethod]
    public void EmptyInput_ReturnsEmptyString()
    {
        Assert.AreEqual(string.Empty, HtmlPlainTextRenderer.StripHtml(string.Empty));
    }
}
