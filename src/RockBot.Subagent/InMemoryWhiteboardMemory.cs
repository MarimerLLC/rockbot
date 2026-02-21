using System.Collections.Concurrent;
using RockBot.Host;

namespace RockBot.Subagent;

/// <summary>
/// In-memory implementation of <see cref="IWhiteboardMemory"/>. Thread-safe, ephemeral across restarts.
/// </summary>
public sealed class InMemoryWhiteboardMemory : IWhiteboardMemory
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _boards = new();

    private ConcurrentDictionary<string, string> GetBoard(string boardId) =>
        _boards.GetOrAdd(boardId, _ => new ConcurrentDictionary<string, string>());

    public Task WriteAsync(string boardId, string key, string value, CancellationToken ct = default)
    {
        GetBoard(boardId).AddOrUpdate(key, value, (_, _) => value);
        return Task.CompletedTask;
    }

    public Task<string?> ReadAsync(string boardId, string key, CancellationToken ct = default)
    {
        GetBoard(boardId).TryGetValue(key, out var value);
        return Task.FromResult<string?>(value);
    }

    public Task DeleteAsync(string boardId, string key, CancellationToken ct = default)
    {
        GetBoard(boardId).TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ListAsync(string boardId, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string> result = GetBoard(boardId).ToDictionary();
        return Task.FromResult(result);
    }

    public Task ClearBoardAsync(string boardId, CancellationToken ct = default)
    {
        if (_boards.TryGetValue(boardId, out var board))
            board.Clear();
        return Task.CompletedTask;
    }
}
