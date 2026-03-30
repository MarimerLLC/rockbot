namespace RockBot.Host;

/// <summary>
/// Persistent log of tool invocations per session.
/// Entries are written during tool execution and queried by the dream system
/// to detect repeated action sequences for skill derivation.
/// </summary>
public interface IToolCallLog
{
    /// <summary>Appends a tool call event to the log.</summary>
    Task AppendAsync(ToolCallEvent evt, CancellationToken ct = default);

    /// <summary>Returns all tool call events for the specified session, ordered by timestamp.</summary>
    Task<IReadOnlyList<ToolCallEvent>> GetBySessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns tool call events recorded on or after <paramref name="since"/>,
    /// ordered by timestamp ascending, capped at <paramref name="maxResults"/>.
    /// </summary>
    Task<IReadOnlyList<ToolCallEvent>> QueryRecentAsync(DateTimeOffset since, int maxResults, CancellationToken ct = default);
}
