namespace RockBot.Host;

/// <summary>
/// Removes directories that a file-backed store has left empty.
///
/// Both the skill store and the embedding cache key files by a name that may contain
/// forward slashes, so writing <c>research/summarize</c> creates a <c>research/</c>
/// directory that nothing cleans up when the last child is deleted. The leftovers are
/// inert on disk but make a directory listing report entries that no longer exist.
///
/// Every method here is best-effort: <see cref="IOException"/> and
/// <see cref="UnauthorizedAccessException"/> are swallowed so a write racing the prune
/// can never fail the caller's operation, and a read-only volume can never block startup.
/// </summary>
internal static class DirectoryPruner
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Deletes <paramref name="directory"/> and every ancestor left empty by it, stopping at
    /// <paramref name="root"/> (never itself removed) or at the first level that still holds
    /// something.
    /// </summary>
    public static void PruneUpward(string root, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        // Containment guard: only ever touch paths strictly below the root.
        while (current.Length > stop.Length
            && current.StartsWith(stop + Path.DirectorySeparatorChar, PathComparison))
        {
            try
            {
                if (Directory.Exists(current))
                {
                    if (Directory.EnumerateFileSystemEntries(current).Any())
                        return;

                    Directory.Delete(current);
                }
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent))
                return;

            current = parent;
        }
    }

    /// <summary>
    /// Removes every empty directory below <paramref name="root"/>, deepest first, and returns
    /// how many were removed. <paramref name="skip"/> is consulted with each candidate's full
    /// path and, when it returns <c>true</c>, that directory and everything under it is left
    /// alone — used to keep a store out of a subdirectory another component owns.
    /// </summary>
    public static int PruneEmptyBelow(string root, Func<string, bool>? skip = null)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var child in children)
        {
            if (skip is not null && skip(child))
                continue;

            removed += PruneEmptyBelow(child, skip);

            try
            {
                if (!Directory.EnumerateFileSystemEntries(child).Any())
                {
                    Directory.Delete(child);
                    removed++;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }
}
