namespace RockBot.Wisp;

/// <summary>
/// Provides read/write access to the shared filesystem volume for wisp data flow.
/// All paths are relative to the shared volume root and validated to prevent directory traversal.
/// </summary>
public interface ISharedVolumeAccessor
{
    /// <summary>
    /// Writes text content to a file on the shared volume.
    /// Creates parent directories as needed.
    /// </summary>
    Task WriteAsync(string relativePath, string content, CancellationToken ct);

    /// <summary>
    /// Reads text content from a file on the shared volume.
    /// Returns null if the file does not exist.
    /// </summary>
    Task<string?> ReadAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// Checks whether a file exists on the shared volume.
    /// </summary>
    Task<bool> ExistsAsync(string relativePath, CancellationToken ct);
}
