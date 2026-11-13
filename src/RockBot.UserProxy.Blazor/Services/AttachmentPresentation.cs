namespace RockBot.UserProxy.Blazor.Services;

/// <summary>How the chat shows one reply attachment.</summary>
public enum AttachmentKind
{
    /// <summary>A link that opens the file in a new tab.</summary>
    Link,

    /// <summary>An inline <c>&lt;img&gt;</c>.</summary>
    Image,

    /// <summary>A sandboxed inline frame showing an HTML document, with a link to open it full-window.</summary>
    HtmlPreview
}

/// <summary>
/// The two decisions about a reply attachment that have to agree: how the chat presents it, and
/// which response headers the <c>/attachments</c> endpoint sends with its bytes. Kept out of the
/// component and the endpoint so both can be unit-tested.
/// </summary>
public static class AttachmentPresentation
{
    /// <summary>
    /// The <c>sandbox</c> tokens for an HTML document written by the agent. Scripts run — a slide
    /// deck is nothing without them — but with no <c>allow-same-origin</c> the document gets an
    /// opaque origin: it cannot read this app's cookies or storage, and cannot reach the Blazor
    /// circuit. The same value is used for the frame attribute and the response header, so the
    /// document is confined whether it is framed in the chat or opened in its own tab.
    /// </summary>
    public const string HtmlSandbox = "allow-scripts";

    /// <summary>How the chat presents an attachment of type <paramref name="mime"/>.</summary>
    public static AttachmentKind KindFor(string? mime)
    {
        if (AttachmentMimeTypes.IsHtml(mime))
            return AttachmentKind.HtmlPreview;
        return mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? AttachmentKind.Image
            : AttachmentKind.Link;
    }

    /// <summary>
    /// The <c>Content-Security-Policy</c> the endpoint sends for a file of type
    /// <paramref name="mime"/>, or null for none. HTML and SVG can both carry script, and served
    /// plainly they would run it in this app's origin when opened directly. SVG gets a full
    /// sandbox: inside an <c>&lt;img&gt;</c> it never ran script anyway. Other types are left
    /// alone — a browser's built-in PDF viewer does not work in a sandboxed document.
    /// </summary>
    public static string? ContentSecurityPolicyFor(string? mime)
    {
        if (AttachmentMimeTypes.IsHtml(mime))
            return $"sandbox {HtmlSandbox}";
        return AttachmentMimeTypes.IsSvg(mime) ? "sandbox" : null;
    }
}
