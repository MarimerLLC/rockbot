namespace RockBot.Host;

/// <summary>
/// Cross-session, concurrent-safe shared scratchpad for data handoff between primary agent and subagents.
/// </summary>
public interface IWhiteboardMemory
{
    Task WriteAsync(string boardId, string key, string value, CancellationToken ct = default);
    Task<string?> ReadAsync(string boardId, string key, CancellationToken ct = default);
    Task DeleteAsync(string boardId, string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> ListAsync(string boardId, CancellationToken ct = default);
    Task ClearBoardAsync(string boardId, CancellationToken ct = default);
}
