using RockBot.Messaging;

namespace RockBot.Host;

/// <summary>
/// Tracks messages that have been pulled from the bus and are being processed.
/// Entries are persisted to disk so incomplete work can be recovered on restart.
/// </summary>
public interface IWipTracker
{
    /// <summary>
    /// Records that processing has begun for the given message.
    /// The envelope is persisted to disk before the handler runs.
    /// </summary>
    Task<WipEntry> BeginAsync(MessageEnvelope envelope, CancellationToken ct = default);

    /// <summary>
    /// Marks processing as successfully completed and removes the WIP entry.
    /// Idempotent — safe to call if the entry was already completed.
    /// </summary>
    Task CompleteAsync(string messageId, CancellationToken ct = default);

    /// <summary>
    /// Marks a WIP entry as abandoned (e.g. too old to recover) and removes it.
    /// The <paramref name="reason"/> is logged for diagnostics.
    /// </summary>
    Task AbandonAsync(string messageId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Returns all WIP entries that have not been completed or abandoned.
    /// Used on startup to discover work interrupted by a crash.
    /// </summary>
    Task<IReadOnlyList<WipEntry>> GetIncompleteAsync(CancellationToken ct = default);
}
