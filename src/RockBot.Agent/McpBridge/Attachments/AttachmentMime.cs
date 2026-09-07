using System.Text;

namespace RockBot.Agent.McpBridge.Attachments;

/// <summary>
/// Filename ↔ MIME mapping shared by the attachment gateway and binary capture, plus the
/// "is this actually binary?" test capture uses to decide whether a payload belongs on disk.
/// </summary>
internal static class AttachmentMime
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/vnd.microsoft.icon",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".md"] = "text/markdown",
        [".json"] = "application/json",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xml"] = "application/xml",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    private static readonly Dictionary<string, string> ExtensionByMime =
        BuildReverse();

    private static Dictionary<string, string> BuildReverse()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // First extension wins so image/jpeg resolves to .jpg rather than .jpeg.
        foreach (var (ext, mime) in ByExtension)
            map.TryAdd(mime, ext);
        return map;
    }

    /// <summary>MIME type for a file name, or <c>application/octet-stream</c> when unknown.</summary>
    public static string FromFileName(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName), out var mime)
            ? mime
            : "application/octet-stream";

    /// <summary>
    /// File extension (including the dot) for a MIME type, or <c>.bin</c> when unknown.
    /// Used to name files captured from typed content blocks, which carry no name of their own.
    /// </summary>
    public static string ToExtension(string? mime) =>
        mime is not null && ExtensionByMime.TryGetValue(mime, out var ext) ? ext : ".bin";

    /// <summary>
    /// Whether a MIME type names content that has no business being rendered as text.
    /// </summary>
    public static bool IsBinaryMime(string? mime) =>
        mime is not null
        && (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/gzip", StringComparison.OrdinalIgnoreCase)
            || mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || mime.StartsWith("application/vnd.openxmlformats", StringComparison.OrdinalIgnoreCase))
        // SVG is an image by MIME but text by nature: the model can read and edit it, and
        // capturing it to disk would take away something it could otherwise work with.
        && !mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a byte sequence looks like binary data rather than text. Used as the fallback
    /// when a payload arrives with no usable name or MIME — a repository server returning a
    /// README and one returning a PNG look identical until you inspect the bytes.
    /// </summary>
    /// <remarks>
    /// A NUL byte is the classic discriminator — it cannot appear in text any tool would hand
    /// back — and strict UTF-8 decoding catches the rest. Only the head is examined: the answer
    /// does not improve by reading a 40 MB file to its end.
    /// </remarks>
    public static bool LooksBinary(ReadOnlySpan<byte> bytes)
    {
        const int HeadLength = 8000;
        var truncated = bytes.Length > HeadLength;
        var head = truncated ? bytes[..HeadLength] : bytes;

        if (head.IndexOf((byte)0) >= 0) return true;

        // A head sliced mid-character ends in a partial UTF-8 sequence that strict decoding
        // would reject, reporting ordinary text as binary. Trim back to a character boundary:
        // drop trailing continuation bytes (10xxxxxx) and then the lead byte above them.
        var end = head.Length;
        if (truncated)
        {
            var floor = Math.Max(0, end - 4);
            while (end > floor && (head[end - 1] & 0xC0) == 0x80) end--;
            if (end > 0 && (head[end - 1] & 0x80) != 0) end--;
        }

        try
        {
            StrictUtf8.GetString(head[..end]);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    private static readonly Encoding StrictUtf8 =
        Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
}
