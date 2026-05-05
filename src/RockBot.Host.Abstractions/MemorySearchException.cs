namespace RockBot.Host;

/// <summary>
/// Thrown by memory search backends when a caller-supplied query cannot be executed —
/// for example an invalid regex pattern, a per-entry match timeout, or an overall
/// scan-budget overrun. The tool layer surfaces the message verbatim to the model so
/// it can refine its query.
/// </summary>
public sealed class MemorySearchException : Exception
{
    public MemorySearchException(string message) : base(message) { }

    public MemorySearchException(string message, Exception inner) : base(message, inner) { }
}
