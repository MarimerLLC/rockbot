namespace RockBot.Host;

/// <summary>Outcome of an archive retention purge.</summary>
/// <param name="Purged">Entries hard-deleted.</param>
/// <param name="Kept">
/// Entries old enough to purge that a caller-supplied predicate held back.
/// </param>
public sealed record ArchivePurgeResult(int Purged, int Kept);
