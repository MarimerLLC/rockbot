using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using RockBot.Agent.McpBridge.Attachments;
using RockBot.Host;
using RockBot.UserProxy;

namespace RockBot.Agent;

/// <summary>
/// Per-session LLM tool that lets the agent attach an image file to its final reply. The file
/// must already exist under the shared attachments directory — produced by a script or MCP
/// tool — so the agent never handles bytes itself (the "Nothing trusts the LLM" rule). The
/// tool validates containment and existence, infers the MIME type from the extension when not
/// given, and stages an <see cref="AgentAttachment"/> in the <see cref="ReplyAttachmentBuffer"/>
/// keyed to this session. The reply-publishing path drains the buffer onto the final reply.
/// </summary>
public sealed class AttachmentReplyTools
{
    private readonly IAttachmentStorage _storage;
    private readonly ReplyAttachmentBuffer _buffer;
    private readonly string _sessionId;
    private readonly ILogger _logger;

    public AttachmentReplyTools(
        IAttachmentStorage storage,
        ReplyAttachmentBuffer buffer,
        string sessionId,
        ILogger logger)
    {
        _storage = storage;
        _buffer = buffer;
        _sessionId = sessionId;
        _logger = logger;

        Tools = [AIFunctionFactory.Create(AttachImage)];
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

        _buffer.Add(_sessionId, new AgentAttachment
        {
            Mime = resolvedMime,
            Path = leaf,
            FileName = displayName
        });

        return $"Attached '{displayName}' ({resolvedMime}). It will be included with your reply.";
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

    private static string GuessMime(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream"
        };
    }
}
