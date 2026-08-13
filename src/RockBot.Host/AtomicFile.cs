namespace RockBot.Host;

/// <summary>
/// Crash-safe UTF-8 text writes for store-owned files.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/> truncates the
/// target before the replacement content is durable, so a cancelled or interrupted write —
/// a pod eviction, a shutdown mid-save — leaves a memory entry or skill as an empty file.
/// Writing to a sibling temporary file and renaming makes the replacement a single
/// directory operation: the file is either the old content or the new one, never neither.
/// </para>
/// <para>
/// This is the encoding-agnostic sibling of the file-tool path's writer. These are files the
/// store itself creates and owns, always UTF-8 JSON or markdown, so there is no original
/// encoding to detect and preserve.
/// </para>
/// </remarks>
internal static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> as UTF-8 (no BOM),
    /// replacing any existing file atomically. Parent directories are created.
    /// </summary>
    internal static async Task WriteAllTextAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            directory = ".";
        else
            Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            CopyUnixFileMode(path, tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Gives the replacement the mode of the file it replaces, so rewriting a file does not
    /// change its permissions. Best-effort: no-ops when the target does not exist yet or the
    /// platform has no Unix modes.
    /// </summary>
    private static void CopyUnixFileMode(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        }
        catch
        {
            // Non-Unix platforms and filesystems without mode support.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The temp file is already the failure path; nothing useful to add.
        }
    }
}
