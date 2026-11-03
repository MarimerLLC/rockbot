using Microsoft.Extensions.AI;
using RockBot.Host;

namespace RockBot.ResearchAgent;

/// <summary>How much a research run may spend.</summary>
internal enum ResearchDepth
{
    /// <summary>
    /// The full run: High tier, up to the 50-iteration cap, as many sources as the question needs.
    /// Minutes long (a broad question measured 7.5 min, 26 sources). The default.
    /// </summary>
    Thorough,

    /// <summary>
    /// A short lookup for a caller on a tight budget (an advisor council's personas): the High
    /// tier like a thorough run, but a handful of tool calls, two or three sources, about a minute.
    /// </summary>
    Quick,
}

/// <summary>What each <see cref="ResearchDepth"/> runs with.</summary>
internal sealed record ResearchDepthProfile(ModelTier Tier, int? MaxToolCalls, TimeSpan? TimeBox, string? PromptNote)
{
    /// <summary>Tool calls a quick run may make before it must answer.</summary>
    internal const int QuickMaxToolCalls = 8;

    /// <summary>
    /// How long a quick run may keep calling tools before it must answer. A call still running
    /// when it ends is cancelled (<see cref="ResearchToolBudget"/>).
    /// </summary>
    internal static readonly TimeSpan QuickTimeBox = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Research runs on the High tier at every depth: the answer is only as good as the model's
    /// judgement of which sources to read and what they establish. Depth bounds the work, not
    /// the model.
    /// </summary>
    public static ResearchDepthProfile For(ResearchDepth depth) => depth switch
    {
        ResearchDepth.Quick => new(
            ModelTier.High, QuickMaxToolCalls, QuickTimeBox,
            "This is a QUICK lookup: run one or two searches, read two or three of the most relevant " +
            "pages, then answer concisely (a few short paragraphs). Do not try to be exhaustive."),
        _ => new(ModelTier.High, null, null, null),
    };

    /// <summary>Reads the tool's <c>depth</c> argument; anything but <c>quick</c> is thorough.</summary>
    public static ResearchDepth Parse(string? value) =>
        string.Equals(value?.Trim(), "quick", StringComparison.OrdinalIgnoreCase)
            ? ResearchDepth.Quick
            : ResearchDepth.Thorough;
}

/// <summary>
/// One run's tool budget: a number of calls and a time box. Once either is spent, every tool
/// answers with a note telling the model to write its answer from what it has, and a call still
/// running when the time box ends is cancelled with that same note.
/// </summary>
/// <remarks>
/// <para>
/// This bounds a quick run without touching the shared function-invoking client's iteration
/// limit, which is per tier and process-wide, so changing it per call would leak into other runs.
/// </para>
/// <para>
/// Checking the box only when a call starts is not enough: one page fetch that hangs runs as long
/// as the HTTP client lets it (a usgs.gov page took 55.6 s to send its headers, so a "60 s" quick
/// run spent 98 s). Each call therefore gets only the time left in the box.
/// </para>
/// </remarks>
internal sealed class ResearchToolBudget(int maxCalls, TimeSpan timeBox, TimeProvider? time = null)
{
    internal const string ExhaustedNote =
        "[Research budget for this quick lookup is used up: do not call more tools. " +
        "Write your answer now from what you have already found, and say what you could not check.]";

    internal const string CutOffNote =
        "[This call was stopped: the quick lookup's time box ran out before it finished, so it returned " +
        "nothing. Do not call more tools. Write your answer now from what you have already found, and " +
        "say what you could not check.]";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly long _started = (time ?? TimeProvider.System).GetTimestamp();
    private int _calls;

    /// <summary>What is left of the time box (zero once it has run out).</summary>
    public TimeSpan Remaining
    {
        get
        {
            var left = timeBox - _time.GetElapsedTime(_started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>Takes one call from the budget; false once calls or time are spent.</summary>
    public bool TryTake() =>
        Remaining > TimeSpan.Zero && Interlocked.Increment(ref _calls) <= maxCalls;

    /// <summary>Wraps <paramref name="tools"/> so each call draws on this budget.</summary>
    public IList<AITool> Wrap(IEnumerable<AITool> tools) =>
        [.. tools.Select(t => t is AIFunction f ? new BudgetedFunction(f, this) : t)];

    private sealed class BudgetedFunction(AIFunction inner, ResearchToolBudget budget) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            if (!budget.TryTake())
                return ExhaustedNote;

            using var timeBoxEnd = new CancellationTokenSource(budget.Remaining, budget._time);
            using var cutOff = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeBoxEnd.Token);
            try
            {
                var result = await base.InvokeCoreAsync(arguments, cutOff.Token).ConfigureAwait(false);
                // A tool that catches its own cancellation returns an error of its own instead of
                // throwing; once the box has run out, the model should read the note either way.
                return timeBoxEnd.IsCancellationRequested ? CutOffNote : result;
            }
            catch (OperationCanceledException) when (timeBoxEnd.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return CutOffNote;
            }
        }
    }
}
