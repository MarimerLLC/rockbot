using Microsoft.Extensions.Logging;

namespace RockBot.Wisp;

/// <summary>
/// File-system-backed shared volume accessor. Validates all paths stay within
/// the configured base path to prevent directory traversal.
/// </summary>
internal sealed class SharedVolumeAccessor(string basePath, ILogger logger) : ISharedVolumeAccessor
{
    public async Task WriteAsync(string relativePath, string content, CancellationToken ct)
    {
        var fullPath = SafeResolvePath(relativePath);
        if (fullPath is null)
            throw new ArgumentException($"Invalid path: '{relativePath}' escapes the shared volume.");

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content, ct);
        logger.LogDebug("Wisp wrote {Length} chars to shared volume: {Path}", content.Length, relativePath);
    }

    public async Task<string?> ReadAsync(string relativePath, CancellationToken ct)
    {
        var fullPath = SafeResolvePath(relativePath);
        if (fullPath is null)
            throw new ArgumentException($"Invalid path: '{relativePath}' escapes the shared volume.");

        if (!File.Exists(fullPath))
            return null;

        var content = await File.ReadAllTextAsync(fullPath, ct);
        logger.LogDebug("Wisp read {Length} chars from shared volume: {Path}", content.Length, relativePath);
        return content;
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken ct)
    {
        var fullPath = SafeResolvePath(relativePath);
        if (fullPath is null)
            return Task.FromResult(false);

        return Task.FromResult(File.Exists(fullPath));
    }

    private string? SafeResolvePath(string relativePath)
    {
        var fullBase = Path.GetFullPath(basePath);
        var fullPath = Path.GetFullPath(Path.Combine(fullBase, relativePath.TrimEnd('/', '\\')));
        return fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }
}
