using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Shared retention utilities for JSONL logs, invoked from the dream cycle. Two
/// shapes are supported: per-session directories of <c>{id}.jsonl</c> files (pruned
/// by age, then by count) and single append-only files (trimmed to a trailing line
/// budget). All operations are best-effort — I/O failures are logged and swallowed
/// so a retention sweep never aborts the caller.
/// </summary>
public static class JsonlLogRetention
{
    /// <summary>
    /// Deletes <paramref name="searchPattern"/> files directly under
    /// <paramref name="directory"/> that are older than <paramref name="maxAge"/>
    /// (by last-write time), then, if more than <paramref name="maxFiles"/> remain,
    /// deletes the oldest until the count is within budget. A non-positive
    /// <paramref name="maxAge"/> disables age pruning; a non-positive
    /// <paramref name="maxFiles"/> disables count pruning. Returns the number of
    /// files deleted.
    /// </summary>
    public static Task<int> PruneAgedFilesAsync(
        string directory,
        TimeSpan maxAge,
        int maxFiles,
        string searchPattern,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directory))
            return Task.FromResult(0);

        var deleted = 0;
        try
        {
            var files = new DirectoryInfo(directory)
                .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
                .ToList();

            // Age-based pruning: drop files not written within the retention window.
            if (maxAge > TimeSpan.Zero)
            {
                var cutoff = DateTime.UtcNow - maxAge;
                foreach (var file in files.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    if (file.LastWriteTimeUtc >= cutoff)
                        continue;
                    if (TryDelete(file, logger))
                    {
                        files.Remove(file);
                        deleted++;
                    }
                }
            }

            // Count-based pruning: keep the most recently written maxFiles.
            if (maxFiles > 0 && files.Count > maxFiles)
            {
                var oldestFirst = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
                var toRemove = files.Count - maxFiles;
                for (var i = 0; i < toRemove; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryDelete(oldestFirst[i], logger))
                        deleted++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JSONL retention: failed to prune directory {Directory}", directory);
        }

        if (deleted > 0)
            logger.LogInformation("JSONL retention: deleted {Count} file(s) from {Directory}", deleted, directory);

        return Task.FromResult(deleted);
    }

    /// <summary>
    /// Rewrites <paramref name="filePath"/> to retain only its last
    /// <paramref name="maxLines"/> lines when it exceeds that budget. A non-positive
    /// <paramref name="maxLines"/> is a no-op. The rewrite is atomic (temp file +
    /// move). Returns the number of lines removed. Callers that also append to this
    /// file MUST serialize this call against their writer.
    /// </summary>
    public static async Task<int> TrimToLastLinesAsync(
        string filePath,
        int maxLines,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (maxLines <= 0 || !File.Exists(filePath))
            return 0;

        try
        {
            var lines = await File.ReadAllLinesAsync(filePath, ct);
            if (lines.Length <= maxLines)
                return 0;

            var keep = lines[^maxLines..];
            var tempPath = filePath + ".tmp";
            await File.WriteAllLinesAsync(tempPath, keep, ct);
            File.Move(tempPath, filePath, overwrite: true);

            var removed = lines.Length - keep.Length;
            logger.LogInformation(
                "JSONL retention: trimmed {Removed} line(s) from {Path} (kept last {Kept})",
                removed, filePath, keep.Length);
            return removed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JSONL retention: failed to trim {Path}", filePath);
            return 0;
        }
    }

    private static bool TryDelete(FileInfo file, ILogger logger)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JSONL retention: failed to delete {Path}", file.FullName);
            return false;
        }
    }
}
