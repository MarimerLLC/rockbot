using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// Reads and writes a store's <see cref="StoreSchemaMarker"/> file.
/// </summary>
internal static class StoreSchemaMarkerFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Full path to the marker within <paramref name="storePath"/>.</summary>
    internal static string PathFor(string storePath) =>
        Path.Combine(storePath, StoreSchemaMarker.FileName);

    /// <summary>
    /// Reads the marker, or returns <c>null</c> when the store has never been stamped.
    /// </summary>
    /// <remarks>
    /// An unreadable marker is reported as absent rather than thrown. A store with a corrupt
    /// marker and real data in it is indistinguishable from a pre-mechanism store, and that is
    /// the safer of the two readings: the caller re-derives the version from
    /// <see cref="StoreSchemaDescriptor.LegacyVersion"/> instead of refusing to start.
    /// </remarks>
    internal static async Task<StoreSchemaMarker?> ReadAsync(
        string storePath,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(storePath);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<StoreSchemaMarker>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex,
                "Unreadable schema marker at {Path}; treating the store as unmarked", path);
            return null;
        }
    }

    /// <summary>
    /// Stamps <paramref name="version"/> into the store's marker, creating the store
    /// directory if it does not exist yet.
    /// </summary>
    internal static async Task WriteAsync(
        string storePath,
        string storeName,
        int version,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(storePath);
        var marker = new StoreSchemaMarker(storeName, version, timestamp);
        var json = JsonSerializer.Serialize(marker, JsonOptions);
        await AtomicFile.WriteAllTextAsync(PathFor(storePath), json, cancellationToken);
    }

    /// <summary>
    /// Whether the store directory holds anything other than its own marker — the test for
    /// "this is an existing store from before the marker existed" rather than a fresh one.
    /// </summary>
    internal static bool HasData(string storePath)
    {
        if (!Directory.Exists(storePath))
            return false;

        return Directory.EnumerateFileSystemEntries(storePath)
            .Any(entry => !string.Equals(
                Path.GetFileName(entry), StoreSchemaMarker.FileName, StringComparison.Ordinal));
    }
}
