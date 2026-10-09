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
    /// failing that, files whose name contains its stem. Never returns backups.
    /// </summary>
    internal static IReadOnlyList<string> FindSimilar(string basePath, string relativePath)
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
                if (FileBackup.IsBackupPath(rel))
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
