namespace RockBot.Host;

/// <summary>
/// Long-term conversation log used to accumulate turn history for preference inference.
/// </summary>
public interface IConversationLog
{
    /// <summary>Appends a single conversation turn to the log.</summary>
    Task AppendAsync(ConversationLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Reads all entries currently in the log.</summary>
    Task<IReadOnlyList<ConversationLogEntry>> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the log. Called by the dream pass after processing.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
