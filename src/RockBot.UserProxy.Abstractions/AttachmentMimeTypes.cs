namespace RockBot.UserProxy;

/// <summary>
/// The file types an <see cref="AgentAttachment"/> can carry to a client, keyed by extension.
/// One table for the agent, which names the type when it attaches a file, and for the client,
/// which sets <c>Content-Type</c> from it when it serves the bytes, so the two cannot disagree.
/// </summary>
public static class AttachmentMimeTypes
{
    /// <summary>MIME type of an HTML document a client may show as a sandboxed preview.</summary>
    public const string Html = "text/html";

    /// <summary>MIME type of an SVG image.</summary>
    public const string Svg = "image/svg+xml";

    /// <summary>Fallback for an extension this table does not know.</summary>
    public const string Unknown = "application/octet-stream";

    /// <summary>
    /// Best-effort MIME type from the extension of <paramref name="fileName"/>;
    /// <see cref="Unknown"/> when the extension is not recognised.
    /// </summary>
    public static string FromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => Svg,
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".html" or ".htm" => Html,
            _ => Unknown
        };
    }

    /// <summary>True when <paramref name="mime"/> is an HTML document, ignoring any charset parameter.</summary>
    public static bool IsHtml(string? mime) => HasMediaType(mime, Html);

    /// <summary>True when <paramref name="mime"/> is SVG, ignoring any parameter.</summary>
    public static bool IsSvg(string? mime) => HasMediaType(mime, Svg);

    private static bool HasMediaType(string? mime, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mime))
            return false;
        var semicolon = mime.IndexOf(';');
        var bare = (semicolon >= 0 ? mime[..semicolon] : mime).Trim();
        return bare.Equals(mediaType, StringComparison.OrdinalIgnoreCase);
    }
}
