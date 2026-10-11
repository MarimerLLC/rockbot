using Microsoft.Extensions.Logging;

namespace RockBot.Host;

/// <summary>
/// One LLM request as measured by <see cref="RequestAccountingChatClient"/> (#670).
/// </summary>
/// <param name="InputTokens">Input tokens the provider reported for this single request.</param>
/// <param name="CachedInputTokens">The cached subset of <paramref name="InputTokens"/>.</param>
/// <param name="OutputTokens">Output tokens the provider reported.</param>
/// <param name="ToolCount">Tools sent with the request; zero for auxiliary calls such as the completion evaluator.</param>
/// <param name="MessageChars">Estimated characters of the message list.</param>
/// <param name="ToolSchemaChars">Characters of the tool names, descriptions and JSON schemas sent.</param>
public sealed record LlmRequestMeasurement(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    int ToolCount,
    long MessageChars,
    long ToolSchemaChars);

/// <summary>
/// Ambient per-run collector of <see cref="LlmRequestMeasurement"/>s (#670). <see cref="AgentLoopRunner.RunAsync"/>
/// opens a scope; every request the run sends — each iteration of the function-invoking loop, the
/// text-based loop's calls, the completion evaluator — is recorded into it, and the scope logs a
/// one-line per-turn summary when it closes. A nested run (a wisp LLM step, an in-flow subagent)
/// opens its own scope, so its requests are not counted against the parent's turn.
/// </summary>
public static class RequestAccountingContext
{
    private static readonly AsyncLocal<State?> Current = new();

    /// <summary>The collector for the current async flow, or null outside any run.</summary>
    public static State? Value => Current.Value;

    /// <summary>
    /// Binds a new collector to the current async flow. Disposing the scope logs the run's summary
    /// (when it sent any request) and restores the previous collector.
    /// </summary>
    public static IDisposable Begin(string? sessionId, ILogger logger, LoopDiagnostics? diagnostics = null)
    {
        var previous = Current.Value;
        var state = new State(sessionId, diagnostics);
        Current.Value = state;
        return new Scope(previous, state, logger);
    }

    /// <summary>Per-run collector state. Thread-safe: parallel tool calls can record concurrently.</summary>
    public sealed class State(string? sessionId, LoopDiagnostics? diagnostics)
    {
        private readonly List<LlmRequestMeasurement> _records = [];
        private readonly Lock _gate = new();

        public string? SessionId { get; } = sessionId;

        public void Record(LlmRequestMeasurement record)
        {
            lock (_gate)
            {
                _records.Add(record);
                if (diagnostics is null) return;
                diagnostics.LlmRequests++;
                if (record.InputTokens > diagnostics.MaxRequestInputTokens)
                    diagnostics.MaxRequestInputTokens = record.InputTokens;
            }
        }

        public IReadOnlyList<LlmRequestMeasurement> Snapshot()
        {
            lock (_gate) return [.. _records];
        }
    }

    /// <summary>
    /// The per-turn figures #670 is judged on. Loop requests (ones that carried tools) are reported
    /// separately from auxiliary ones, which are small and would otherwise flatter the median.
    /// </summary>
    public sealed record Summary(
        int Requests,
        int AuxRequests,
        long InputMedian,
        long InputMax,
        long InputSum,
        long CachedSum,
        long OutputSum,
        int MaxTools,
        long MaxToolSchemaChars);

    public static Summary Summarize(IReadOnlyList<LlmRequestMeasurement> records)
    {
        var loop = records.Where(r => r.ToolCount > 0).ToList();
        var inputs = loop.Select(r => r.InputTokens).Order().ToList();
        return new Summary(
            Requests: loop.Count,
            AuxRequests: records.Count - loop.Count,
            InputMedian: Median(inputs),
            InputMax: inputs.Count > 0 ? inputs[^1] : 0,
            InputSum: records.Sum(r => r.InputTokens),
            CachedSum: records.Sum(r => r.CachedInputTokens),
            OutputSum: records.Sum(r => r.OutputTokens),
            MaxTools: loop.Count > 0 ? loop.Max(r => r.ToolCount) : 0,
            MaxToolSchemaChars: loop.Count > 0 ? loop.Max(r => r.ToolSchemaChars) : 0);
    }

    internal static long Median(IReadOnlyList<long> sorted) => sorted.Count switch
    {
        0 => 0,
        var n when n % 2 == 1 => sorted[n / 2],
        var n => (sorted[n / 2 - 1] + sorted[n / 2]) / 2,
    };

    private sealed class Scope(State? previous, State state, ILogger logger) : IDisposable
    {
        public void Dispose()
        {
            Current.Value = previous;
            var records = state.Snapshot();
            if (records.Count == 0) return;

            var s = Summarize(records);
            logger.LogInformation(
                "Turn context summary: session={Session} requests={Requests} aux={Aux} " +
                "inputMedian={Median} inputMax={Max} inputSum={Sum} cachedSum={Cached} outputSum={Output} " +
                "maxTools={MaxTools} maxToolSchemaChars={SchemaChars}",
                state.SessionId, s.Requests, s.AuxRequests, s.InputMedian, s.InputMax, s.InputSum,
                s.CachedSum, s.OutputSum, s.MaxTools, s.MaxToolSchemaChars);
        }
    }
}
