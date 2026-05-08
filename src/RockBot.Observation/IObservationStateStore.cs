namespace RockBot.Observation;

/// <summary>
/// Atomic reader/writer for an <see cref="ObservationState"/> file. Implementations
/// must guarantee that a partially-written file is never observed: writes go to
/// a temp path and are renamed into place. A crash mid-write leaves the canonical
/// file untouched.
/// </summary>
public interface IObservationStateStore
{
    /// <summary>
    /// Reads the state for the given target. Returns a fresh empty
    /// <see cref="ObservationState"/> if the file does not exist.
    /// </summary>
    Task<ObservationState> LoadAsync(ObservationTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Writes the state for the given target atomically. Implementations must
    /// write to a temporary path, fsync, and rename to the canonical path so
    /// that readers never observe a partially-written file.
    /// </summary>
    Task SaveAsync(ObservationTarget target, ObservationState state, CancellationToken cancellationToken);
}
