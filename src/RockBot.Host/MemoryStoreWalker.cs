using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Reads the memory store's files directly off disk, without going through
/// <see cref="FileMemoryStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The audit must not instantiate a second store. <c>FileMemoryStore</c>'s index load prunes
/// empty category directories as a side effect, so an auditor that used one would delete the
/// very thing it is trying to count — and would then report zero every time.
/// </para>
/// <para>
/// Reading the files also means archived entries, malformed files and orphaned directories are
/// all visible. A store hides archived entries from search by design; the audit's entire job is
/// to look at what the store is hiding.
/// </para>
/// </remarks>
internal static class MemoryStoreWalker
{
    /// <summary>
    /// Matches <see cref="FileMemoryStore"/>'s serializer settings so an entry round-trips
    /// identically whichever reader opens it.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>What one walk of the memory root found.</summary>
    /// <param name="Entries">Every entry that deserialized, live and archived alike.</param>
    /// <param name="EmptyCategoryDirs">Directories under the root holding no entry files at all.</param>
    /// <param name="MalformedFiles">Files that would not deserialize and were skipped.</param>
    internal sealed record WalkResult(
        IReadOnlyList<MemoryEntry> Entries,
        int EmptyCategoryDirs,
        int MalformedFiles);

    /// <summary>
    /// Walks <paramref name="memoryRoot"/> recursively. A missing root is not an error — a fresh
    /// agent has no store yet, and the audit should report an empty corpus rather than fail.
    /// </summary>
    internal static async Task<WalkResult> WalkAsync(
        string memoryRoot,
        ILogger logger,
        CancellationToken ct = default)
    {
        var entries = new List<MemoryEntry>();
        var malformed = 0;

        if (!Directory.Exists(memoryRoot))
            return new WalkResult(entries, 0, 0);

        foreach (var file in Directory.EnumerateFiles(memoryRoot, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            // The embedding cache owns its own folder of .bin files and one .json manifest;
            // neither is a memory entry.
            if (IsUnderEmbeddings(memoryRoot, file))
                continue;

            try
            {
                var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
                var entry = JsonSerializer.Deserialize<MemoryEntry>(json, JsonOptions);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Id))
                    entries.Add(entry);
                else
                    malformed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                malformed++;
                logger.LogWarning(ex, "Memory audit: skipping unreadable memory file {Path}", file);
            }
        }

        return new WalkResult(entries, CountEmptyCategoryDirs(memoryRoot), malformed);
    }

    /// <summary>
    /// Counts directories below the root that contain no entry file anywhere beneath them.
    /// A parent whose only content is a populated child is doing its job and is not counted.
    /// </summary>
    private static int CountEmptyCategoryDirs(string memoryRoot)
    {
        var count = 0;

        foreach (var dir in Directory.EnumerateDirectories(memoryRoot, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(dir), EmbeddingCache.DirectoryName, StringComparison.Ordinal))
                continue;
            if (IsUnderEmbeddings(memoryRoot, dir))
                continue;

            if (!Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Any())
                count++;
        }

        return count;
    }

    private static bool IsUnderEmbeddings(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, EmbeddingCache.DirectoryName, StringComparison.Ordinal));
    }
}
