namespace RockBot.UserProxy.Blazor.Services;

/// <summary>
/// Resolves the <c>file</c> query parameter of the <c>/attachments</c> endpoint to an absolute
/// path under the shared attachments directory, rejecting any input that escapes the base
/// (parent-directory traversal, absolute paths pointing elsewhere). The Blazor pod co-mounts the
/// shared PVC read-only; this is the containment boundary that keeps the endpoint from serving
/// arbitrary files. Factored out of <c>Program.cs</c> so the boundary can be unit-tested.
/// </summary>
public sealed class AttachmentPathResolver
{
    /// <summary>Absolute base path of the attachments directory (e.g. <c>/rockbot/shared/attachments</c>).</summary>
    public string BasePath { get; }

    public AttachmentPathResolver(string basePath)
    {
        BasePath = Path.GetFullPath(basePath ?? throw new ArgumentNullException(nameof(basePath)));
    }

    /// <summary>
    /// Resolves the base attachments directory from <c>ROCKBOT_SHARED_PATH</c>, falling back to
    /// <c>/rockbot/shared</c> when the env var is unset — matching <c>AttachmentStorage</c>.
    /// </summary>
    public static AttachmentPathResolver FromEnvironment()
    {
        var sharedRoot = Environment.GetEnvironmentVariable("ROCKBOT_SHARED_PATH");
        if (string.IsNullOrWhiteSpace(sharedRoot))
            sharedRoot = "/rockbot/shared";
        return new AttachmentPathResolver(Path.Combine(sharedRoot, "attachments"));
    }

    /// <summary>
    /// Resolves <paramref name="file"/> (a bare filename or relative path under the base) to an
    /// absolute path that exists inside <see cref="BasePath"/>. Returns <c>null</c> when the input
    /// is empty, escapes the base, or the file does not exist.
    /// </summary>
    public string? Resolve(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
            return null;

        string candidate;
        try
        {
            // Combine treats a rooted `file` as overriding the base; the containment check below
            // catches that case and rejects it. Strip a redundant leading "attachments/" so the
            // path convention used elsewhere resolves to a single layer.
            var normalized = StripRedundantBaseLeaf(file);
            candidate = Path.GetFullPath(Path.Combine(BasePath, normalized));
        }
        catch (ArgumentException)
        {
            return null; // invalid path characters
        }

        var baseWithSep = BasePath.EndsWith(Path.DirectorySeparatorChar)
            ? BasePath
            : BasePath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(candidate) ? candidate : null;
    }

    private string StripRedundantBaseLeaf(string relativePath)
    {
        var leaf = Path.GetFileName(BasePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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

    /// <summary>
    /// Best-effort MIME lookup from the file extension, for the <c>Content-Type</c> response header.
    /// </summary>
    public static string GuessMime(string fileName)
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
