namespace RockBot.UserProxy;

/// <summary>
/// Resolves the file a client names on an <c>/attachments</c> endpoint (the Blazor UI's
/// <c>file</c> query parameter, the AG-UI proxy's path) to an absolute path under the shared
/// attachments directory, rejecting any input that escapes the base (parent-directory traversal,
/// absolute paths pointing elsewhere). A user proxy co-mounts the shared volume read-only; this is
/// the containment boundary that keeps its endpoint from serving arbitrary files. Shared by the
/// proxies that serve attachments, so there is one boundary to test.
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

    /// <summary>Longest attachment reference <see cref="IsSafeRelativePath"/> accepts.</summary>
    public const int MaxRelativePathLength = 512;

    /// <summary>
    /// True when <paramref name="path"/> is a plain relative path that cannot leave whatever
    /// directory it is later resolved under: the form of <see cref="AgentAttachment.Path"/> on the
    /// wire. For an attachment reference a client hands back (on a message it sends), checked
    /// before the reference is passed on. Purely lexical, and the same on every platform, so a
    /// proxy on Linux refuses what an agent on Windows would misread and the reverse: no rooted
    /// path, no drive or stream (<c>:</c>), no backslash, no <c>.</c> or <c>..</c> segment, no
    /// empty segment, no control character.
    /// </summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxRelativePathLength)
            return false;

        foreach (var c in path)
        {
            if (c is '\\' or ':' || char.IsControl(c))
                return false;
        }

        // A leading or doubled slash leaves an empty segment, which the loop refuses.
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment != segment.Trim())
                return false;
        }
        return true;
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
    public static string GuessMime(string fileName) => AttachmentMimeTypes.FromFileName(fileName);
}
