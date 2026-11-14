namespace RockBot.AgUi.Client.Tests;

/// <summary>The sample's Markdown renderer: small, and never a way to get HTML onto the page.</summary>
[TestClass]
public sealed class SafeMarkdownTests
{
    [TestMethod]
    public void HtmlInAReply_IsShownAsText_NeverRendered()
    {
        var html = SafeMarkdown.ToHtml("Hi <script>alert(1)</script> <img src=x onerror=alert(1)> **<b>bold</b>**");

        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("<img", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(html, "&lt;script&gt;alert(1)&lt;/script&gt;");
        StringAssert.Contains(html, "<strong>&lt;b&gt;bold&lt;/b&gt;</strong>");
    }

    [TestMethod]
    [DataRow("[x](javascript:alert(1))")]
    [DataRow("[x](data:text/html,<script>1</script>)")]
    [DataRow("[x](https://ok.example/\" onclick=\"alert(1))")]
    public void ALink_IsOnlyMadeForAnHttpAddress_AndCantBreakOutOfItsAttribute(string markdown)
    {
        var html = SafeMarkdown.ToHtml(markdown);

        Assert.IsFalse(html.Contains("javascript:", StringComparison.OrdinalIgnoreCase) && html.Contains("href=\"javascript", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("href=\"data:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(html.Contains("\" onclick=\"", StringComparison.Ordinal), html);
    }

    [TestMethod]
    public void TheMarkdownTheSampleDeclares_IsRendered()
    {
        var html = SafeMarkdown.ToHtml("""
            # Title

            Some **bold**, *italic* and `code <b>` with a [link](https://example.com/a?b=1&c=2).

            > ↳ Re: "Daily summary"
            > started 09:00 from scheduler

            - one
            - two

            1. first
            2. second

            ```csharp
            var x = a < b;
            ```
            """);

        StringAssert.Contains(html, "<h3>Title</h3>");
        StringAssert.Contains(html, "<strong>bold</strong>");
        StringAssert.Contains(html, "<em>italic</em>");
        StringAssert.Contains(html, "<code>code &lt;b&gt;</code>");
        StringAssert.Contains(html, "<a href=\"https://example.com/a?b=1&amp;c=2\" target=\"_blank\" rel=\"noopener noreferrer\">link</a>");
        StringAssert.Contains(html, "<blockquote>↳ Re: &quot;Daily summary&quot;<br>started 09:00 from scheduler</blockquote>");
        StringAssert.Contains(html, "<ul><li>one</li><li>two</li></ul>");
        StringAssert.Contains(html, "<ol><li>first</li><li>second</li></ol>");
        StringAssert.Contains(html, "<pre><code>var x = a &lt; b;</code></pre>");
    }

    [TestMethod]
    public void LinesOfAParagraph_KeepTheirBreaks_AndEmptyInputIsEmpty()
    {
        Assert.AreEqual("<p>one<br>two</p><p>three</p>", SafeMarkdown.ToHtml("one\ntwo\n\nthree"));
        Assert.AreEqual("", SafeMarkdown.ToHtml(null));
    }
}
