namespace RockBot.Host;

/// <summary>
/// The contents of a store's on-disk schema version marker.
/// </summary>
/// <remarks>
/// Written to <see cref="FileName"/> in the store's root directory. The name is deliberately
/// dot-prefixed and extension-less: several stores build their index by enumerating
/// <c>*.json</c> or <c>*.jsonl</c> beneath their root, and a marker matching those patterns
/// would be picked up as a corrupt record.
/// </remarks>
/// <param name="Store">The owning store's name. A mismatch means two stores share a directory.</param>
/// <param name="Version">The schema version the data in this directory is at.</param>
/// <param name="UpdatedAt">When the marker was last stamped.</param>
public sealed record StoreSchemaMarker(string Store, int Version, DateTimeOffset UpdatedAt)
{
    /// <summary>File name of the marker within a store's root directory.</summary>
    public const string FileName = ".rockbot-schema";
}
