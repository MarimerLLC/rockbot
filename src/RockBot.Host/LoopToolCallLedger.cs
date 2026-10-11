namespace RockBot.Host;

/// <summary>One tool call made during a single <see cref="AgentLoopRunner.RunAsync"/> run.</summary>
/// <param name="Name">The tool name as the model called it.</param>
/// <param name="Arguments">Compact argument summary or JSON, used to resolve the inner tool name of
/// generic proxies such as <c>mcp_invoke_tool</c>. May be null.</param>
/// <param name="Succeeded">False when the call threw or returned an error result, or when the tool
/// reported through <see cref="ToolCallOutcomeContext"/> that work it ran failed (#686).</param>
public sealed record LoopToolCall(string Name, string? Arguments, bool Succeeded)
{
    /// <summary>
    /// The tool's own account of its outcome (#686), e.g. <c>6 of 7 wisps failed</c>. For a call
    /// made inside a batch tool, where it ran, e.g. <c>wisp wisp-9eaa… step create</c>. Null when none.
    /// </summary>
    public string? Detail { get; init; }

    /// <summary>
    /// The calls a batch tool such as <c>spawn_wisps</c> made on the agent's behalf (#686), in
    /// order, or null. Without them the evaluator saw one successful <c>spawn_wisps</c> where six of
    /// seven nested creates had failed.
    /// </summary>
    public IReadOnlyList<LoopToolCall>? Nested { get; init; }
}

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
    private readonly Action<string, IEnumerable<KeyValuePair<string, object?>>?, bool>? _observer;

    /// <summary>A ledger with no observer.</summary>
    public LoopToolCallLedger() { }

    /// <summary>
    /// A ledger that also hands every recorded call, with its full arguments, to
    /// <paramref name="observer"/> — the one hook through which every tool call of the run passes,
    /// native and text paths alike. The session work registry (#665) records artifacts this way.
    /// An observer that throws is ignored.
    /// </summary>
    public LoopToolCallLedger(Action<string, IEnumerable<KeyValuePair<string, object?>>?, bool>? observer)
    {
        _observer = observer;
    }

    /// <summary>Records a finished (or failed) tool call.</summary>
    /// <param name="name">The tool name as the model called it.</param>
    /// <param name="arguments">Shortened argument summary kept in the ledger.</param>
    /// <param name="succeeded">False when the call threw or returned an error result.</param>
    /// <param name="rawArguments">The call's full arguments, for the observer only. May be null.</param>
    /// <param name="outcome">What the tool reported about its own outcome (#686), or null. Its
    /// <see cref="ToolCallOutcome.Detail"/> and <see cref="ToolCallOutcome.Nested"/> calls are kept on the entry.</param>
    public void Record(string name, string? arguments, bool succeeded,
        IEnumerable<KeyValuePair<string, object?>>? rawArguments = null,
        ToolCallOutcome? outcome = null)
    {
        if (string.IsNullOrEmpty(name)) return;
        var nested = outcome?.NestedSnapshot();
        var call = new LoopToolCall(name, arguments, succeeded)
        {
            Detail = outcome?.Detail,
            Nested = nested is { Count: > 0 } ? nested : null,
        };
        lock (_lock) _calls.Add(call);
        if (_observer is null) return;
        try
        {
            _observer(name, rawArguments, succeeded);
        }
        catch
        {
            // Observation is best-effort; it must never fail the tool call.
        }
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
/// What a tool reports about its own outcome beyond its result text (#686). A batch tool such as
/// <c>spawn_wisps</c> returns a non-error result even when some of its work failed — marking the
/// result as an error would make the model re-run the parts that worked — so it reports the
/// failure, and the calls it made, here. The tool-calling paths bind a fresh instance around each
/// call (<see cref="ToolCallOutcomeContext"/>) and record it in the ledger and the tool-call log.
/// Thread-safe.
/// </summary>
public sealed class ToolCallOutcome
{
    private readonly List<LoopToolCall> _nested = [];
    private readonly Lock _lock = new();

    /// <summary>False when the tool reports that work it ran failed; null when it reported nothing.</summary>
    public bool? Succeeded { get; private set; }

    /// <summary>The tool's own summary of its outcome, e.g. <c>6 of 7 wisps failed</c>, or null.</summary>
    public string? Detail { get; private set; }

    /// <summary>Records the tool's overall outcome.</summary>
    public void Report(bool succeeded, string? detail)
    {
        lock (_lock)
        {
            Succeeded = succeeded;
            Detail = detail;
        }
    }

    /// <summary>Adds calls the tool made on the agent's behalf.</summary>
    public void AddNested(IEnumerable<LoopToolCall> calls)
    {
        lock (_lock) _nested.AddRange(calls);
    }

    /// <summary>A snapshot of the nested calls recorded so far.</summary>
    public IReadOnlyList<LoopToolCall> NestedSnapshot()
    {
        lock (_lock) return [.. _nested];
    }

    /// <summary>
    /// One line per nested call for the tool-call log, e.g.
    /// <c>calendar-mcp__create_event FAILED (wisp wisp-9eaa… step create)</c>; null when there are none.
    /// </summary>
    public static IReadOnlyList<string>? FormatNested(IReadOnlyList<LoopToolCall>? nested) =>
        nested is { Count: > 0 }
            ? nested.Select(c => $"{c.Name} {(c.Succeeded ? "ok" : "FAILED")}{(c.Detail is null ? "" : $" ({c.Detail})")}").ToList()
            : null;
}

/// <summary>
/// Ambient per-async-flow <see cref="ToolCallOutcome"/> of the tool call running on this flow
/// (#686). Bound by <see cref="RockBotFunctionInvokingChatClient"/> and the text-based loop around
/// each tool invocation; null outside one, so a tool that reports must tolerate its absence.
/// </summary>
public static class ToolCallOutcomeContext
{
    private static readonly AsyncLocal<ToolCallOutcome?> Current = new();

    /// <summary>The outcome holder of the tool call running on this async flow, or null.</summary>
    public static ToolCallOutcome? Value => Current.Value;

    /// <summary>Binds <paramref name="outcome"/> to the current async flow until the scope is disposed.</summary>
    public static IDisposable Set(ToolCallOutcome? outcome)
    {
        var previous = Current.Value;
        Current.Value = outcome;
        return new Scope(previous);
    }

    private sealed class Scope(ToolCallOutcome? previous) : IDisposable
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
