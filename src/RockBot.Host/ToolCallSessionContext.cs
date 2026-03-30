namespace RockBot.Host;

/// <summary>
/// Ambient session context for tool-call logging.
/// Set by <see cref="AgentLoopRunner"/> before entering the LLM loop so that
/// <see cref="RockBotFunctionInvokingChatClient"/> can tag tool-call events
/// with the correct session ID without requiring it as a constructor parameter.
/// </summary>
public static class ToolCallSessionContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>Gets the current session ID, or null if not set.</summary>
    public static string? SessionId => Current.Value;

    /// <summary>
    /// Sets the session ID for the current async flow. Returns a disposable that
    /// restores the previous value on dispose.
    /// </summary>
    public static IDisposable Set(string? sessionId)
    {
        var previous = Current.Value;
        Current.Value = sessionId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
