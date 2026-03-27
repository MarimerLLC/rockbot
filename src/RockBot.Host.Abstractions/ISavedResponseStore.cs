namespace RockBot.Host;

/// <summary>
/// Stores user-saved agent responses for later retrieval.
/// </summary>
public interface ISavedResponseStore
{
    Task SaveAsync(SavedResponse response, CancellationToken cancellationToken = default);
    Task<SavedResponse?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SavedResponse>> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
