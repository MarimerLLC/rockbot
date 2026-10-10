using System.Collections.Concurrent;
using System.Text;

namespace RockBot.Tools.FileSystem;

/// <summary>
/// One lock per resolved path, shared by every tool that rewrites a file. Concurrent writes
/// and edits to the same file serialize, and writes to different files do not.
/// </summary>
/// <remarks>
/// Entries are never evicted. That costs one small object per distinct file written in the
/// process lifetime, bounded by the volume's contents.
/// </remarks>
internal static class FileLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static SemaphoreSlim For(string fullPath) => Locks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
}

/// <summary>
/// One-generation backups of files that <c>file_write</c> or <c>file_edit</c> replace, kept
/// under <see cref="DirectoryName"/> at the volume root, mirroring the original's relative path.
/// </summary>
/// <remarks>
/// <c>file_list</c> hides this directory unless asked for it by prefix, so it does not
/// clutter the agent's view of the volume. Each overwrite replaces the previous backup.
/// That is enough to undo the last mistake, which is the case that matters (issue #664).
/// The volume's normal expiry sweep removes backups like any other file.
/// </remarks>
internal static class FileBackup
{
    /// <summary>Backup root, relative to the volume root.</summary>
    public const string DirectoryName = ".prev";

    /// <summary>Whether <paramref name="relativePath"/> is itself inside the backup directory.</summary>
    public static bool IsBackupPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.Equals(DirectoryName, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(DirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saves <paramref name="previousBytes"/> as the backup of <paramref name="fullPath"/>.
    /// Returns the backup's volume-relative path, or <c>null</c> plus an error when the file
    /// lives in the backup directory or the backup could not be written.
    /// </summary>
    public static async Task<(string? RelativePath, string? Error)> KeepAsync(
        string basePath, string fullPath, byte[] previousBytes, CancellationToken ct)
    {
        var fullBase = Path.GetFullPath(basePath);
        var relative = Path.GetRelativePath(fullBase, fullPath).Replace('\\', '/');
        if (IsBackupPath(relative))
            return (null, null);

        var backupRelative = $"{DirectoryName}/{relative}";
        var backupFull = Path.Combine(fullBase, DirectoryName, relative);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(backupFull)!);
            await File.WriteAllBytesAsync(backupFull, previousBytes, ct);
            FileModes.MakeShared(backupFull);
            return (backupRelative, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Sentence for a tool result describing where the backup went, or why there is none.</summary>
    public static string Describe((string? RelativePath, string? Error) backup) => backup switch
    {
        ({ } path, _) => $" The previous version is kept at {path} until the next change to this file.",
        (null, { } error) => $" (No backup of the previous version could be kept: {error})",
        _ => string.Empty,
    };
}

/// <summary>Unix mode helpers for files other containers on the volume must be able to rewrite.</summary>
internal static class FileModes
{
    /// <summary>
    /// Makes <paramref name="fullPath"/> world-readable and world-writable, so other containers
    /// sharing the volume (script pods, MCP servers) can read and overwrite it. Best effort.
    /// </summary>
    public static void MakeShared(string fullPath)
    {
        try
        {
            File.SetUnixFileMode(fullPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        catch
        {
            // Non-Unix platforms — best-effort only.
        }
    }
}

/// <summary>
/// Builds the "file not found" message for tools that need an existing file.
/// </summary>
/// <remarks>
/// The message this replaces ended "Use file_write to create it." In issue #664 the missing
/// path was a OneDrive path, not a volume path, and the real file sat at
/// <c>drafts/&lt;name&gt;</c>. Pointing the model at <c>file_write</c> there invites it to
/// create a second copy or overwrite the wrong file. So the message names same-named files
/// that do exist and says plainly that remote paths are not volume files.
/// </remarks>
internal static class MissingFile
{
    private const int MaxSuggestions = 5;
    private const int MaxFilesScanned = 50_000;

    public static string Describe(string basePath, string relativePath, bool forEdit)
    {
        var sb = new StringBuilder();
        sb.Append($"File not found on the shared volume: {relativePath}.");

        var matches = FindSimilar(basePath, relativePath);
        if (matches.Count > 0)
        {
            sb.Append(" Files with the same name that do exist: ");
            sb.Append(string.Join(", ", matches));
            sb.Append(". If you meant one of these, use that path.");
        }

        sb.Append(" Paths from remote services (OneDrive, SharePoint, email attachments, MCP servers) are not files "
            + "on the shared volume, and file tools cannot read or change them. To change a remote file, work on "
            + "its shared-volume copy and upload that copy again.");

        if (forEdit)
        {
            sb.Append(" file_edit only changes files that already exist. Use file_list to find the right path. "
                + "Use file_write only when you mean to create a brand-new file.");
        }
        else
        {
            sb.Append(" Use file_list to see what is on the volume.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Volume files whose name matches the requested file's name (case-insensitively), or
    /// failing that, files whose name contains its stem. Never returns backups, nor any
    /// volume-relative path for which <paramref name="exclude"/> returns <c>true</c>.
    /// </summary>
    internal static IReadOnlyList<string> FindSimilar(
        string basePath, string relativePath, Func<string, bool>? exclude = null)
    {
        var fileName = Path.GetFileName(relativePath.Replace('\\', '/').TrimEnd('/'));
        if (string.IsNullOrEmpty(fileName))
            return [];

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var fullBase = Path.GetFullPath(basePath);
        if (!Directory.Exists(fullBase))
            return [];

        var exact = new List<string>();
        var partial = new List<string>();
        var scanned = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(fullBase, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            }))
            {
                if (++scanned > MaxFilesScanned)
                    break;

                var rel = Path.GetRelativePath(fullBase, file).Replace('\\', '/');
                if (FileBackup.IsBackupPath(rel) || exclude?.Invoke(rel) == true)
                    continue;

                var name = Path.GetFileName(rel);
                if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    exact.Add(rel);
                else if (stem.Length >= 4 && name.Contains(stem, StringComparison.OrdinalIgnoreCase))
                    partial.Add(rel);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A suggestion list is a courtesy; an unreadable corner of the volume is not an error.
        }

        var chosen = exact.Count > 0 ? exact : partial;
        chosen.Sort(StringComparer.Ordinal);
        return chosen.Take(MaxSuggestions).ToList();
    }
}

/// <summary>Write-permission checks and the messages for a refused write.</summary>
internal static class WriteAccess
{
    /// <summary>
    /// Whether the file itself can be opened for writing, independent of its directory.
    /// </summary>
    public static bool CanWrite(string fullPath)
    {
        try
        {
            using var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is a permission failure: <see cref="UnauthorizedAccessException"/>
    /// (EACCES/EPERM on Unix, access denied or a read-only attribute on Windows), or an
    /// <see cref="IOException"/> reporting access denied or a read-only filesystem (EROFS).
    /// </summary>
    public static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException
        || (ex is IOException io
            && (io.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || io.Message.Contains("read-only file system", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// The attachments area (<see cref="FileSystemOptions.AttachmentsDirectory"/>) and the error
/// for a write into it that the filesystem refused.
/// </summary>
/// <remarks>
/// In issue #677 a model downloaded a OneDrive deck into <c>attachments/</c>, failed to
/// write it back there with a bare "Access to the path … is denied", and then went looking
/// for some other path to write to. It ended up creating a renamed sibling copy. The message
/// here says what <c>attachments/</c> is for and names the <c>drafts/</c> file to edit instead.
/// </remarks>
internal static class AttachmentsArea
{
    /// <summary>Whether <paramref name="fullPath"/> lies inside the attachments directory.</summary>
    public static bool Contains(FileSystemOptions options, string fullPath)
    {
        var dir = ConfiguredDirectory(options);
        if (dir is null)
            return false;

        var fullDir = Path.GetFullPath(Path.Combine(Path.GetFullPath(options.BasePath), dir));
        var candidate = Path.GetFullPath(fullPath);
        return candidate.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(fullDir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.Equals(fullDir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The error for a refused write to <paramref name="relativePath"/> in the attachments
    /// area. Names a same-name file outside the attachments area, preferring <c>drafts/</c>,
    /// when one exists.
    /// </summary>
    public static string Describe(FileSystemOptions options, string relativePath, string? systemError)
    {
        var dir = ConfiguredDirectory(options) ?? "attachments";
        var name = Path.GetFileName(relativePath.Replace('\\', '/').TrimEnd('/'));
        var match = FindMatchingDraft(options, relativePath);

        var sb = new StringBuilder();
        sb.Append($"Cannot change {relativePath}: {dir}/ holds downloaded files and is read-only for file tools.");
        if (match is not null)
        {
            sb.Append($" Edit the matching file under drafts/ instead: {match} exists (file_read it, then use "
                + "file_edit or file_write). If it is not the same document, copy the content to a new drafts/ "
                + "path and work there.");
        }
        else
        {
            sb.Append($" Edit the matching file under drafts/ (e.g. drafts/{name}) if one exists, or copy the "
                + "content to a new drafts/ path and work there.");
        }

        if (!string.IsNullOrWhiteSpace(systemError))
            sb.Append($" (System error: {systemError})");

        return sb.ToString();
    }

    /// <summary>
    /// A volume file outside the attachments area with the same name as
    /// <paramref name="relativePath"/>, preferring one under <c>drafts/</c>; <c>null</c> if none.
    /// </summary>
    internal static string? FindMatchingDraft(FileSystemOptions options, string relativePath)
    {
        var name = Path.GetFileName(relativePath.Replace('\\', '/').TrimEnd('/'));
        if (string.IsNullOrEmpty(name))
            return null;

        var dir = ConfiguredDirectory(options);
        var exact = MissingFile.FindSimilar(options.BasePath, relativePath,
                exclude: rel => dir is not null
                    && (rel.Equals(dir, StringComparison.OrdinalIgnoreCase)
                        || rel.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)))
            .Where(rel => Path.GetFileName(rel).Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return exact.FirstOrDefault(rel => rel.StartsWith("drafts/", StringComparison.OrdinalIgnoreCase))
            ?? exact.FirstOrDefault();
    }

    /// <summary>The configured directory, normalized to forward slashes without surrounding ones; null if disabled.</summary>
    private static string? ConfiguredDirectory(FileSystemOptions options)
    {
        var dir = options.AttachmentsDirectory?.Replace('\\', '/').Trim('/');
        return string.IsNullOrWhiteSpace(dir) ? null : dir;
    }
}

/// <summary>
/// Spots a new file that looks like a renamed copy of one whose overwrite was just refused,
/// e.g. <c>deck-revised.md</c> after <c>deck.md</c>. Diagnostic only (issue #677): it tells us
/// whether the refusal text keeps the model from working around the guard.
/// </summary>
internal static partial class SiblingCopy
{
    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[-_. ]?(revised|revision|rev\d*|v\d+|new|copy|updated|update|edited|edit|final|fixed|modified|\d+)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Suffix();

    /// <summary>
    /// Whether <paramref name="newPath"/> is <paramref name="originalPath"/>'s name plus a
    /// copy-style suffix, with the same extension. Directories are not compared.
    /// </summary>
    public static bool IsLikely(string newPath, string originalPath)
    {
        var newName = Path.GetFileName(newPath);
        var oldName = Path.GetFileName(originalPath);
        if (newName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Path.GetExtension(newName).Equals(Path.GetExtension(oldName), StringComparison.OrdinalIgnoreCase))
            return false;

        var newStem = Path.GetFileNameWithoutExtension(newName);
        var oldStem = Path.GetFileNameWithoutExtension(oldName);
        if (oldStem.Length == 0 || !newStem.StartsWith(oldStem, StringComparison.OrdinalIgnoreCase))
            return false;

        return Suffix().IsMatch(newStem[oldStem.Length..]);
    }
}

/// <summary>
/// Finds the region of a file closest to an <c>old_string</c> that did not match, so a failed
/// <c>file_edit</c> shows the model the text it should have copied.
/// </summary>
/// <remarks>
/// In issue #664 a model copied <c>old_string</c> from a subagent's summary instead of the
/// file. The bare "not found" left it nothing to work with, and it fell back to rewriting the
/// whole file. This class compares each of the first few non-blank lines of
/// <c>old_string</c> with every line of the file using a character-bigram Dice score. It
/// picks the best line and shows a numbered excerpt around it.
/// </remarks>
internal static class NearestRegion
{
    private const int CandidateLines = 5;
    private const int ContextLines = 2;
    private const int MaxExcerptLines = 20;
    private const int MaxDisplayChars = 300;
    private const int MaxCompareChars = 300;
    private const double MinScore = 0.5;

    public static string Describe(string content, string oldString)
    {
        var fileLines = SplitLines(content);
        var oldLines = SplitLines(oldString);

        var candidates = oldLines
            .Select((text, index) => (Text: Normalize(text), Index: index))
            .Where(c => c.Text.Length > 0)
            .Take(CandidateLines)
            .ToList();

        if (candidates.Count == 0 || fileLines.Count == 0)
            return string.Empty;

        var normalizedFile = fileLines.Select(Normalize).ToArray();
        var bestScore = 0.0;
        var bestFileLine = -1;
        var bestOldLine = 0;

        foreach (var candidate in candidates)
        {
            var candidateBigrams = Bigrams(candidate.Text);
            for (var i = 0; i < normalizedFile.Length; i++)
            {
                var score = normalizedFile[i] == candidate.Text
                    ? 1.0
                    : Dice(candidateBigrams, normalizedFile[i]);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestFileLine = i;
                    bestOldLine = candidate.Index;
                }
            }

            if (bestScore >= 1.0)
                break;
        }

        if (bestFileLine < 0 || bestScore < MinScore)
        {
            return " No line of the file resembles old_string. It may have been copied from a summary, a "
                + "message, or another file rather than from this file. Read the file with file_read and copy "
                + "the text verbatim.";
        }

        var anchor = Math.Max(0, bestFileLine - bestOldLine);
        var first = Math.Max(0, anchor - ContextLines);
        var last = Math.Min(fileLines.Count - 1, anchor + oldLines.Count - 1 + ContextLines);
        last = Math.Min(last, first + MaxExcerptLines - 1);

        var sb = new StringBuilder();
        var exactLine = bestScore >= 1.0;
        sb.Append(exactLine
            ? $" Line {bestFileLine + 1} of the file matches line {bestOldLine + 1} of old_string (ignoring "
              + "whitespace and letter case), but the text as a whole does not match."
            : $" The closest match is near line {bestFileLine + 1} of the file.");
        sb.Append($" Lines {first + 1}–{last + 1} as they are now (the numbers and '| ' are not part of the "
            + "file; copy only the text after them):");
        sb.AppendLine();

        var width = (last + 1).ToString().Length;
        for (var i = first; i <= last; i++)
        {
            var text = fileLines[i];
            if (text.Length > MaxDisplayChars)
                text = text[..MaxDisplayChars] + "…";
            sb.Append((i + 1).ToString().PadLeft(width)).Append("| ").AppendLine(text);
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>Lower-cased, whitespace-collapsed, trimmed, length-capped form for comparison.</summary>
    private static string Normalize(string line)
    {
        var sb = new StringBuilder(Math.Min(line.Length, MaxCompareChars));
        var pendingSpace = false;
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(char.ToLowerInvariant(c));
            if (sb.Length >= MaxCompareChars)
                break;
        }
        return sb.ToString();
    }

    private static Dictionary<int, int> Bigrams(string s)
    {
        var counts = new Dictionary<int, int>();
        for (var i = 0; i + 1 < s.Length; i++)
        {
            var key = (s[i] << 16) | s[i + 1];
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    /// <summary>Sørensen–Dice coefficient over character bigram multisets.</summary>
    private static double Dice(Dictionary<int, int> a, string b)
    {
        var aTotal = a.Values.Sum();
        var bTotal = Math.Max(0, b.Length - 1);
        if (aTotal == 0 || bTotal == 0)
            return 0;

        var remaining = new Dictionary<int, int>(a);
        var shared = 0;
        for (var i = 0; i + 1 < b.Length; i++)
        {
            var key = (b[i] << 16) | b[i + 1];
            if (remaining.TryGetValue(key, out var n) && n > 0)
            {
                remaining[key] = n - 1;
                shared++;
            }
        }

        return 2.0 * shared / (aTotal + bTotal);
    }
}
