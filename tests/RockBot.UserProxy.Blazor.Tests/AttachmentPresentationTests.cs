using RockBot.UserProxy.Blazor.Services;

namespace RockBot.UserProxy.Blazor.Tests;

[TestClass]
public class AttachmentPresentationTests
{
    [TestMethod]
    [DataRow("text/html", AttachmentKind.HtmlPreview)]
    [DataRow("TEXT/HTML; charset=utf-8", AttachmentKind.HtmlPreview)]
    [DataRow("image/png", AttachmentKind.Image)]
    [DataRow("image/svg+xml", AttachmentKind.Image)]
    [DataRow("application/pdf", AttachmentKind.Link)]
    [DataRow("application/octet-stream", AttachmentKind.Link)]
    [DataRow("", AttachmentKind.Link)]
    [DataRow(null, AttachmentKind.Link)]
    public void KindFor(string? mime, AttachmentKind expected)
    {
        Assert.AreEqual(expected, AttachmentPresentation.KindFor(mime));
    }

    [TestMethod]
    public void Html_IsSandboxed_WithScripts_ButNeverSameOrigin()
    {
        var csp = AttachmentPresentation.ContentSecurityPolicyFor("text/html");

        Assert.AreEqual("sandbox allow-scripts", csp);
        // allow-same-origin would hand an agent-written document this app's cookies and circuit.
        Assert.IsFalse(AttachmentPresentation.HtmlSandbox.Contains("allow-same-origin"));
        Assert.IsFalse(AttachmentPresentation.HtmlSandbox.Contains("allow-top-navigation"));
    }

    [TestMethod]
    public void Svg_IsFullySandboxed()
    {
        Assert.AreEqual("sandbox", AttachmentPresentation.ContentSecurityPolicyFor("image/svg+xml"));
    }

    [TestMethod]
    [DataRow("image/png")]
    [DataRow("application/pdf")] // a browser's PDF viewer does not run in a sandboxed document
    [DataRow("application/octet-stream")]
    public void OtherTypes_GetNoPolicy(string mime)
    {
        Assert.IsNull(AttachmentPresentation.ContentSecurityPolicyFor(mime));
    }

    [TestMethod]
    [DataRow("text/html", "text/html; charset=utf-8")]
    [DataRow("text/html; charset=iso-8859-1", "text/html; charset=iso-8859-1")] // an explicit one stands
    [DataRow("image/png", "image/png")]
    [DataRow("image/svg+xml", "image/svg+xml")] // XML declares its own encoding
    [DataRow("application/pdf", "application/pdf")]
    public void ContentTypeFor_DeclaresUtf8_ForHtmlOnly(string mime, string expected)
    {
        Assert.AreEqual(expected, AttachmentPresentation.ContentTypeFor(mime));
    }

    [TestMethod]
    public void MimeTable_KnowsHtml()
    {
        Assert.AreEqual("text/html", AttachmentPathResolver.GuessMime("deck.html"));
        Assert.AreEqual("text/html", AttachmentPathResolver.GuessMime("deck.HTM"));
    }
}
