namespace RockBot.Host;

/// <summary>
/// Persistent store for <see cref="RepairTicket"/> artifacts. One JSON file per
/// ticket on the PVC; concurrent updates are safe via temp+rename writes.
/// See <c>design/self-repair.md</c> Phase 4.
/// </summary>
public interface IRepairTicketStore
{
    /// <summary>Returns every ticket currently on disk, ordered by <see cref="RepairTicket.UpdatedAt"/> descending.</summary>
    Task<IReadOnlyList<RepairTicket>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns tickets whose <see cref="RepairTicket.Status"/> is <see cref="RepairStatus.Open"/> or <see cref="RepairStatus.InProgress"/>.</summary>
    Task<IReadOnlyList<RepairTicket>> ListOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the ticket with the given id, or <c>null</c> if no such file exists.</summary>
    Task<RepairTicket?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the ticket on disk. Atomic via temp file + rename.</summary>
    Task SaveAsync(RepairTicket ticket, CancellationToken cancellationToken = default);

    /// <summary>Removes the ticket. No-op if no such file exists.</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
