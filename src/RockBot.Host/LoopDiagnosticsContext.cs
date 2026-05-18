namespace RockBot.Host;

/// <summary>
/// Ambient per-async-flow handle to a <see cref="LoopDiagnostics"/> instance the
/// caller of <see cref="AgentLoopRunner.RunAsync"/> wants the loop infrastructure
/// to populate. Set by the runner; read by <see cref="RockBotFunctionInvokingChatClient"/>
/// so the native tool path can record per-tool-call state from inside a singleton
/// without plumbing the diagnostics object through the M.E.AI middleware chain.
/// </summary>
public static class LoopDiagnosticsContext
{
    private static readonly AsyncLocal<LoopDiagnostics?> Current = new();

    /// <summary>Gets the active diagnostics instance, or null when no caller is collecting.</summary>
    public static LoopDiagnostics? Value => Current.Value;

    /// <summary>
    /// Binds <paramref name="diagnostics"/> to the current async flow. Returns a
    /// disposable that restores the previous value on dispose.
    /// </summary>
    public static IDisposable Set(LoopDiagnostics? diagnostics)
    {
        var previous = Current.Value;
        Current.Value = diagnostics;
        return new Scope(previous);
    }

    private sealed class Scope(LoopDiagnostics? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
