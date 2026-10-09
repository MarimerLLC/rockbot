namespace RockBot.Host;

/// <summary>One tool call made during a single <see cref="AgentLoopRunner.RunAsync"/> run.</summary>
/// <param name="Name">The tool name as the model called it.</param>
/// <param name="Arguments">Compact argument summary or JSON, used to resolve the inner tool name of
/// generic proxies such as <c>mcp_invoke_tool</c>. May be null.</param>
/// <param name="Succeeded">False when the call threw or returned an error result.</param>
public sealed record LoopToolCall(string Name, string? Arguments, bool Succeeded);

/// <summary>
/// The tool calls made during one <see cref="AgentLoopRunner.RunAsync"/> run, in order. Unlike
/// the chat history, which context trimming can shorten, this keeps every call, so the completion
/// evaluator's gate (#666) can tell "made zero tool calls" from "the calls were trimmed away".
/// Thread-safe: the native path may invoke tools concurrently.
/// </summary>
public sealed class LoopToolCallLedger
{
    private readonly List<LoopToolCall> _calls = [];
    private readonly Lock _lock = new();

    /// <summary>Records a finished (or failed) tool call.</summary>
    public void Record(string name, string? arguments, bool succeeded)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_lock) _calls.Add(new LoopToolCall(name, arguments, succeeded));
    }

    /// <summary>A snapshot of the calls recorded so far.</summary>
    public IReadOnlyList<LoopToolCall> Snapshot()
    {
        lock (_lock) return [.. _calls];
    }
}

/// <summary>
/// Ambient per-async-flow handle to the <see cref="LoopToolCallLedger"/> of the running loop. Set
/// by <see cref="AgentLoopRunner.RunAsync"/>; written by <see cref="RockBotFunctionInvokingChatClient"/>
/// (native path) and the text-based loop, which is how a singleton middleware reaches per-run state.
/// </summary>
public static class LoopToolCallLedgerContext
{
    private static readonly AsyncLocal<LoopToolCallLedger?> Current = new();

    /// <summary>The ledger of the loop running on this async flow, or null outside a loop.</summary>
    public static LoopToolCallLedger? Value => Current.Value;

    /// <summary>Binds <paramref name="ledger"/> to the current async flow until the scope is disposed.</summary>
    public static IDisposable Set(LoopToolCallLedger? ledger)
    {
        var previous = Current.Value;
        Current.Value = ledger;
        return new Scope(previous);
    }

    private sealed class Scope(LoopToolCallLedger? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Ambient per-async-flow text of the user request the running loop serves (#666). Set by
/// <see cref="AgentLoopRunner.RunAsync"/>; read by tools that start work whose result comes back
/// later — <c>spawn_subagent</c> records it with the task so the synthesis turn can be checked
/// against what the user actually asked, not just the subagent's self-report.
/// </summary>
public static class OriginatingUserRequestContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The user request of the loop running on this async flow, or null.</summary>
    public static string? Value => Current.Value;

    /// <summary>Binds <paramref name="request"/> to the current async flow until the scope is disposed.</summary>
    public static IDisposable Set(string? request)
    {
        var previous = Current.Value;
        Current.Value = request;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
