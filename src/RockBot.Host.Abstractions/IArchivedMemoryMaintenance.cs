namespace RockBot.Host;

/// <summary>
/// Optional capability for stores that keep an archive tier — implemented alongside
/// <see cref="ILongTermMemory.ArchiveAsync"/> to manage what has accumulated there.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="ILongTermMemory"/> because archiving is a retention policy,
/// not part of the read/write contract: a store backed by something with its own versioning
/// or soft-delete semantics can satisfy <see cref="ILongTermMemory"/> without implementing
/// any of this. Callers probe for it (<c>memory as IArchivedMemoryMaintenance</c>) and skip
/// the work when absent.
/// </remarks>
public interface IArchivedMemoryMaintenance
{
    /// <summary>
    /// Returns an archived entry to normal visibility.
    /// </summary>
    /// <returns><c>true</c> if an archived entry was restored; <c>false</c> if it was not
    /// found or was not archived.</returns>
    Task<bool> RestoreAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hard-deletes archived entries older than <paramref name="retention"/>, measured from
    /// <see cref="MemoryEntry.ArchivedAt"/>. Returns the number purged. A non-positive
    /// retention disables purging so archived entries are kept indefinitely.
    /// </summary>
    Task<int> PurgeArchivedAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}
