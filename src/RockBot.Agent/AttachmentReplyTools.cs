using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Per-session LLM tools that let the agent attach a file to its final reply. The file must
/// already exist on the shared volume — produced by a script, an MCP tool or the file tools —
/// so the agent never handles bytes itself (the "Nothing trusts the LLM" rule). Each tool
/// validates containment and existence, infers the MIME type from the extension, and stages an
/// <see cref="AgentAttachment"/> in the <see cref="ReplyAttachmentBuffer"/> keyed to this
/// (session, turn). The reply-publishing path drains the buffer onto the final reply.
/// <para>
/// <c>attach_image</c> takes a file already under the attachments directory. <c>attach_file</c>
/// takes a file anywhere on the shared volume — the file tools cannot write into
/// <c>attachments/</c>, so a deck written to <c>exports/</c> is copied there first — and is how an
/// HTML document reaches a client that can preview it (#669).
/// </para>
/// </summary>
public sealed class AttachmentReplyTools
{
    private readonly IAttachmentStorage _storage;
    private readonly ReplyAttachmentBuffer _buffer;
    private readonly string _sessionId;
    private readonly string _turnId;
    private readonly ILogger _logger;
    private readonly ClientCapabilities _capabilities;
    private readonly string _sharedRoot;

    /// <summary>Largest file <c>attach_file</c> accepts. A client fetches the whole file to show it.</summary>
    internal const long MaxAttachFileBytes = 20 * 1024 * 1024;

    /// <summary>Subdirectory of the attachments directory that holds copies made by <c>attach_file</c>.</summary>
    internal const string PreviewsDirectory = "previews";

    /// <param name="capabilities">What the client receiving this turn's reply can render.</param>
    /// <param name="sharedRoot">
    /// Root of the shared volume, which <c>attach_file</c> paths are relative to. Defaults to the
    /// parent of the attachments directory.
    /// </param>
    public AttachmentReplyTools(
        IAttachmentStorage storage,
        ReplyAttachmentBuffer buffer,
        string sessionId,
        string turnId,
        ILogger logger,
        ClientCapabilities capabilities = ClientCapabilities.None,
        string? sharedRoot = null)
    {
        _storage = storage;
        _buffer = buffer;
        _sessionId = sessionId;
        _turnId = turnId;
        _logger = logger;
        _capabilities = capabilities;
        _sharedRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(sharedRoot)
                ? Path.GetDirectoryName(Path.GetFullPath(storage.BasePath).TrimEnd(
                      Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? storage.BasePath
                : sharedRoot);

        // Tool names are pinned to snake_case rather than inherited from the method
        // names: every prompt and directive in the repo refers to them that way, and
        // pointing the model at a name it cannot call is what produced issue #493.
        Tools =
        [
            AIFunctionFactory.Create(AttachImage,
                new AIFunctionFactoryOptions { Name = "attach_image" }),
            AIFunctionFactory.Create(AttachFile,
                new AIFunctionFactoryOptions { Name = "attach_file" })
        ];
    }

    public IList<AITool> Tools { get; }

    [Description(
        "Attach an image file to your reply so the user sees it rendered inline (e.g. a chart, " +
        "diagram, or screenshot). The file must already exist in the shared attachments directory " +
        "— first have a script or tool write it there, then call this with the filename. Do NOT " +
        "embed images as markdown or data URLs; those are stripped. Only call this once the file " +
        "exists. The image is shown to clients that can render it; others see a short placeholder line.")]
    public string AttachImage(
        [Description("Filename of the image under the shared attachments directory (e.g. 'chart.png'). " +
                     "Must be a file that already exists there.")] string path,
        [Description("Optional MIME type (e.g. 'image/png'). Inferred from the file extension when omitted.")] string? mime = null,
        [Description("Optional friendly display name shown to the user. Defaults to the filename.")] string? fileName = null)
    {
        _logger.LogInformation("Tool call: AttachImage(path={Path}, mime={Mime})", path, mime);

        if (string.IsNullOrWhiteSpace(path))
            return "Could not attach: no path provided. Pass the filename of an image already written to the shared attachments directory.";

        string resolved;
        try
        {
            resolved = ResolveUnderBase(path);
        }
        catch (UnauthorizedAccessException)
        {
            return $"Could not attach '{path}': it is outside the shared attachments directory. " +
                   "Only files under the shared attachments directory can be attached.";
        }

        if (!File.Exists(resolved))
            return $"Could not attach '{path}': no such file in the shared attachments directory. " +
                   "Write the image there first, then attach it.";

        var resolvedMime = string.IsNullOrWhiteSpace(mime) ? GuessMime(resolved) : mime.Trim();
        var leaf = Path.GetFileName(path);
        var displayName = string.IsNullOrWhiteSpace(fileName) ? leaf : fileName.Trim();

        _buffer.Add(_sessionId, _turnId, new AgentAttachment
        {
            Mime = resolvedMime,
            Path = leaf,
            FileName = displayName
        });

        return $"Attached '{displayName}' ({resolvedMime}). It will be included with your reply.";
    }

    [Description(
        "Attach a file from the shared volume to your reply so the user can open it: an HTML " +
        "document (a slide deck, a report page), a PDF, an SVG or an image. Clients that can " +
        "preview HTML show the document live in the chat, with a link to open it full-window; " +
        "the result tells you whether this user's client can. Use this instead of pasting HTML " +
        "into your reply or telling the user where a file is. The file must already exist " +
        "(write it with file_write or a script first) and an HTML file must be self-contained: " +
        "one file, with any libraries loaded from a public CDN. See the 'presentations' tool " +
        "guide before building a deck.")]
    public string AttachFile(
        [Description("Path of the file on the shared volume, as the file tools name it " +
                     "(e.g. 'exports/q3-review.html'). Allowed types: .html, .pdf, .svg, .png, " +
                     ".jpg, .gif, .webp, .bmp.")] string path,
        [Description("Optional friendly display name shown to the user. Defaults to the filename.")] string? fileName = null)
    {
        _logger.LogInformation("Tool call: AttachFile(path={Path})", path);

        if (string.IsNullOrWhiteSpace(path))
            return "Could not attach: no path provided. Pass the shared-volume path of a file that already exists.";

        var resolved = ResolveUnderSharedRoot(path);
        if (resolved is null)
            return $"Could not attach '{path}': it is outside the shared volume. " +
                   "Only files on the shared volume can be attached.";

        var info = new FileInfo(resolved);
        if (!info.Exists)
            return $"Could not attach '{path}': no such file on the shared volume. " +
                   "Write the file first, then attach it.";

        var mime = AttachmentMimeTypes.FromFileName(resolved);
        if (mime == AttachmentMimeTypes.Unknown)
            return $"Could not attach '{path}': that file type cannot be shown to the user. " +
                   "Allowed types: .html, .pdf, .svg, .png, .jpg, .gif, .webp, .bmp. " +
                   "For a deck or a report, produce one self-contained .html file.";

        if (info.Length > MaxAttachFileBytes)
            return $"Could not attach '{path}': it is {info.Length:N0} bytes and the limit is " +
                   $"{MaxAttachFileBytes:N0}. Reduce the file (for HTML, link large media instead of embedding it).";

        string relative;
        try
        {
            relative = PlaceUnderAttachments(resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "attach_file could not copy {Path} into the attachments directory", resolved);
            return $"Could not attach '{path}': copying it into the attachments directory failed ({ex.Message}).";
        }

        var displayName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(resolved) : fileName.Trim();
        _buffer.Add(_sessionId, _turnId, new AgentAttachment
        {
            Mime = mime,
            Path = relative,
            FileName = displayName
        });

        return $"Attached '{displayName}' ({mime}). {DescribeDelivery(mime)}";
    }

    /// <summary>
    /// Tells the model what the user will actually see, so it does not promise a preview to a
    /// client that prints a placeholder line.
    /// </summary>
    private string DescribeDelivery(string mime)
    {
        const string GiveAnotherWay =
            "Also give them a way to open it, such as uploading the file to their cloud storage and sharing the link.";

        if (AttachmentMimeTypes.IsHtml(mime))
        {
            return _capabilities.HasFlag(ClientCapabilities.HtmlPreview)
                ? "The user's client shows it as a live preview in your reply, with a link to open it full-window. " +
                  "Do not also paste the HTML or a file path."
                : "The user's client cannot preview HTML: they will see only a placeholder line. " + GiveAnotherWay;
        }

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return _capabilities.HasFlag(ClientCapabilities.ImageAttachment)
                ? "The user's client shows the image in your reply."
                : "The user's client cannot show images: they will see only a placeholder line.";
        }

        return _capabilities.HasFlag(ClientCapabilities.ImageAttachment)
               || _capabilities.HasFlag(ClientCapabilities.HtmlPreview)
            ? "The user's client shows it as a link that opens the file."
            : "The user's client cannot open attachments: they will see only a placeholder line. " + GiveAnotherWay;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> under the shared volume root, or returns null when it
    /// escapes it. Relative paths are relative to the root, as the file tools name them.
    /// </summary>
    private string? ResolveUnderSharedRoot(string path)
    {
        string candidate;
        try
        {
            candidate = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_sharedRoot, path));
        }
        catch (ArgumentException)
        {
            return null; // invalid path characters
        }

        return IsUnder(_sharedRoot, candidate) ? candidate : null;
    }

    /// <summary>
    /// Returns the path of <paramref name="resolved"/> relative to the attachments directory, the
    /// form an <see cref="AgentAttachment"/> carries. A file elsewhere on the shared volume is
    /// first copied to <c>previews/&lt;id&gt;/&lt;name&gt;</c> there: clients serve the attachments
    /// directory only, and the copy is what the user saw even if the source is edited later.
    /// </summary>
    private string PlaceUnderAttachments(string resolved)
    {
        var attachmentsBase = Path.GetFullPath(_storage.BasePath);
        if (IsUnder(attachmentsBase, resolved))
            return Path.GetRelativePath(attachmentsBase, resolved).Replace('\\', '/');

        var id = Guid.NewGuid().ToString("N")[..8];
        var leaf = Path.GetFileName(resolved);
        var targetDir = Path.Combine(attachmentsBase, PreviewsDirectory, id);
        Directory.CreateDirectory(targetDir);
        File.Copy(resolved, Path.Combine(targetDir, leaf), overwrite: false);
        return $"{PreviewsDirectory}/{id}/{leaf}";
    }

    private static bool IsUnder(string baseDir, string candidate)
    {
        var trimmed = baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to an absolute path under <see cref="IAttachmentStorage.BasePath"/>,
    /// throwing <see cref="UnauthorizedAccessException"/> if it escapes the base. Mirrors
    /// <c>AttachmentStorage.ResolveReadPath</c> so model-controlled input cannot reach arbitrary
    /// filesystem locations.
    /// </summary>
    private string ResolveUnderBase(string path)
    {
        var fullBase = Path.GetFullPath(_storage.BasePath);

        string candidate;
        if (Path.IsPathRooted(path))
        {
            candidate = Path.GetFullPath(path);
        }
        else
        {
            // The shared-volume convention refers to files as `<subdir>/<file>`; the model may
            // write `attachments/foo.png` even though BasePath already ends in `/attachments`.
            // Strip the redundant leaf so it resolves to a single layer.
            candidate = Path.GetFullPath(Path.Combine(fullBase, StripRedundantBaseLeaf(fullBase, path)));
        }

        var baseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar)
            ? fullBase
            : fullBase + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase)
            && !candidate.Equals(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Attachment path '{path}' is outside the shared attachments directory '{_storage.BasePath}'.");
        }

        return candidate;
    }

    private static string StripRedundantBaseLeaf(string basePath, string relativePath)
    {
        var leaf = Path.GetFileName(basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf)) return relativePath;

        ReadOnlySpan<char> span = relativePath;
        if (span.StartsWith(leaf, StringComparison.OrdinalIgnoreCase)
            && span.Length > leaf.Length
            && (span[leaf.Length] == '/' || span[leaf.Length] == '\\'))
        {
            return relativePath[(leaf.Length + 1)..];
        }
        return relativePath;
    }

    private static string GuessMime(string fileName) => AttachmentMimeTypes.FromFileName(fileName);
}
