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
    /// <see cref="MemoryEntry.ArchivedAt"/>. A non-positive retention disables purging so
    /// archived entries are kept indefinitely.
    /// </summary>
    /// <param name="retention">How long an archived entry is kept before it may be purged.</param>
    /// <param name="keep">
    /// Optional veto over an entry that is otherwise due. Entries it accepts are left on disk and
    /// counted as kept.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The purge is the one place left that hard-deletes memory, so it is also the last place a
    /// value judgement can be applied. Callers pass the same high-value floor that stops
    /// consolidation pruning an entry outright — an entry archived by a merge nobody reviewed is
    /// exactly the one worth keeping recoverable past the retention window.
    /// </remarks>
    Task<ArchivePurgeResult> PurgeArchivedAsync(
        TimeSpan retention,
        Func<MemoryEntry, bool>? keep = null,
        CancellationToken cancellationToken = default);
}
