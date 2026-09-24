using RockBot.UserProxy.Cli;

namespace RockBot.Cli.Tests;

[TestClass]
public sealed class SpectreMarkupConverterTests
{
    [TestMethod]
    public void PlainText_PassesThroughUnchanged()
    {
        Assert.AreEqual("hello world", SpectreMarkupConverter.ToSpectreMarkup("hello world"));
    }

    [TestMethod]
    public void NamedColorSpan_TranslatedToSpectreMarkup()
    {
        Assert.AreEqual(
            "[red]x[/]",
            SpectreMarkupConverter.ToSpectreMarkup("<span style=\"color:red\">x</span>"));
    }

    [TestMethod]
    public void GreyAlias_PreservedAsLowercaseNamedColor()
    {
        Assert.AreEqual(
            "[grey]x[/]",
            SpectreMarkupConverter.ToSpectreMarkup("<span style=\"color:GREY\">x</span>"));
    }

    [TestMethod]
    public void HexColorSpan_PassesThroughAsSpectreColor()
    {
        Assert.AreEqual(
            "[#ff0000]x[/]",
            SpectreMarkupConverter.ToSpectreMarkup("<span style=\"color:#ff0000\">x</span>"));
    }

    [TestMethod]
    public void UnknownColor_FallsBackToInnerTextWithoutMarkup()
    {
        Assert.AreEqual(
            "danger",
            SpectreMarkupConverter.ToSpectreMarkup("<span style=\"color:rebeccapurple\">danger</span>"));
    }

    [TestMethod]
    public void StrongTag_TranslatedToBold()
    {
        Assert.AreEqual("[bold]x[/]", SpectreMarkupConverter.ToSpectreMarkup("<strong>x</strong>"));
    }

    [TestMethod]
    public void BTag_TranslatedToBold()
    {
        Assert.AreEqual("[bold]x[/]", SpectreMarkupConverter.ToSpectreMarkup("<b>x</b>"));
    }

    [TestMethod]
    public void Svg_ReplacedWithEscapedChartPlaceholder()
    {
        var input = "<svg width=\"10\"><rect/></svg>";
        // The placeholder text uses square brackets, which must be Spectre-escaped
        // so the user sees the literal text rather than a Spectre parse error.
        Assert.AreEqual(
            "[[chart — view in Blazor]]",
            SpectreMarkupConverter.ToSpectreMarkup(input));
    }

    [TestMethod]
    public void SquareBrackets_InPlainText_AreEscaped()
    {
        Assert.AreEqual("[[foo]]", SpectreMarkupConverter.ToSpectreMarkup("[foo]"));
    }

    [TestMethod]
    public void SquareBrackets_InsideTranslatedTagContent_AreEscaped()
    {
        Assert.AreEqual(
            "[red][[foo]][/]",
            SpectreMarkupConverter.ToSpectreMarkup("<span style=\"color:red\">[foo]</span>"));
    }

    [TestMethod]
    public void SquareBrackets_InsideBoldTagContent_AreEscaped()
    {
        Assert.AreEqual(
            "[bold][[bar]][/]",
            SpectreMarkupConverter.ToSpectreMarkup("<strong>[bar]</strong>"));
    }

    [TestMethod]
    public void KnownTag_Stripped_InnerTextRetained()
    {
        Assert.AreEqual(
            "rows",
            SpectreMarkupConverter.ToSpectreMarkup("<table>rows</table>"));
    }

    [TestMethod]
    public void NullInput_ReturnsEmptyString()
    {
        Assert.AreEqual(string.Empty, SpectreMarkupConverter.ToSpectreMarkup(null));
    }

    [TestMethod]
    public void EmptyInput_ReturnsEmptyString()
    {
        Assert.AreEqual(string.Empty, SpectreMarkupConverter.ToSpectreMarkup(string.Empty));
    }

    [TestMethod]
    public void PathPlaceholders_PreservedVerbatim()
    {
        var input = "Expected '<folder>/<uidvalidity>/<uid>'";
        Assert.AreEqual(input, SpectreMarkupConverter.ToSpectreMarkup(input));
    }

    [TestMethod]
    public void Placeholder_InsideBoldTagContent_Preserved()
    {
        Assert.AreEqual(
            "[bold]pass <uid>[/]",
            SpectreMarkupConverter.ToSpectreMarkup("<strong>pass <uid></strong>"));
    }

    [TestMethod]
    public void Placeholder_NextToSquareBrackets_BothPreserved()
    {
        Assert.AreEqual(
            "[[<folder>]]",
            SpectreMarkupConverter.ToSpectreMarkup("[<folder>]"));
    }
}
