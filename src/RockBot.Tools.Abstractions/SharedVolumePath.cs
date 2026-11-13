namespace RockBot.Tools;

/// <summary>
/// Resolves a model-supplied path against the shared volume and refuses anything that would
/// leave it. Script pods run LLM-written code against the same volume, so "inside the volume"
/// has to mean the file itself, not just its name: a symbolic link planted there can point at
/// the agent's own files, and reading or copying through it would carry them out.
/// </summary>
public static class SharedVolumePath
{
    /// <summary>
    /// Resolves <paramref name="path"/> (relative to <paramref name="basePath"/>, or absolute) to
    /// a full path strictly inside <paramref name="basePath"/>. Returns null when the path is
    /// empty or malformed, when it escapes the base, or when the file or any directory between
    /// it and the base is a symbolic link. The file need not exist.
    /// </summary>
    public static string? Resolve(string basePath, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullBase, full;
        try
        {
            fullBase = Path.GetFullPath(basePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Combine lets a rooted path replace the base; the containment check below decides.
            full = Path.GetFullPath(Path.Combine(fullBase, path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!IsUnder(fullBase, full))
            return null;

        return PassesThroughLink(fullBase, full) ? null : full;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is strictly inside <paramref name="fullBase"/>. The
    /// comparison includes the separator, so <c>/shared-other</c> is not inside <c>/shared</c>.
    /// </summary>
    public static bool IsUnder(string fullBase, string fullPath)
    {
        var trimmed = fullBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="fullPath"/>, or a directory between it and
    /// <paramref name="fullBase"/>, is a symbolic link. The base itself may be one: that is the
    /// operator's choice of mount. Components that do not exist yet are not links.
    /// </summary>
    public static bool PassesThroughLink(string fullBase, string fullPath)
    {
        var trimmedBase = fullBase.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        while (current.Length > trimmedBase.Length && IsUnder(trimmedBase, current))
        {
            try
            {
                // FileInfo reads the link itself, for a file or a directory, existing or dangling.
                if (new FileInfo(current).LinkTarget is not null)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true; // cannot tell, so do not trust it
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
                break;
            current = parent;
        }

        return false;
    }
}
