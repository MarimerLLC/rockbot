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
    /// A short lookup for a caller on a tight budget (an advisor council's personas): the Balanced
    /// tier, a handful of tool calls, two or three sources, about a minute.
    /// </summary>
    Quick,
}

/// <summary>What each <see cref="ResearchDepth"/> runs with.</summary>
internal sealed record ResearchDepthProfile(ModelTier Tier, int? MaxToolCalls, TimeSpan? TimeBox, string? PromptNote)
{
    /// <summary>Tool calls a quick run may make before it must answer.</summary>
    internal const int QuickMaxToolCalls = 8;

    /// <summary>How long a quick run may keep calling tools before it must answer.</summary>
    internal static readonly TimeSpan QuickTimeBox = TimeSpan.FromSeconds(60);

    public static ResearchDepthProfile For(ResearchDepth depth) => depth switch
    {
        ResearchDepth.Quick => new(
            ModelTier.Balanced, QuickMaxToolCalls, QuickTimeBox,
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
/// answers with a note telling the model to write its answer from what it has.
/// </summary>
/// <remarks>
/// This bounds a quick run without touching the shared function-invoking client's iteration
/// limit, which is per tier and process-wide, so changing it per call would leak into other runs.
/// </remarks>
internal sealed class ResearchToolBudget(int maxCalls, TimeSpan timeBox, TimeProvider? time = null)
{
    internal const string ExhaustedNote =
        "[Research budget for this quick lookup is used up: do not call more tools. " +
        "Write your answer now from what you have already found, and say what you could not check.]";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly long _started = (time ?? TimeProvider.System).GetTimestamp();
    private int _calls;

    /// <summary>Takes one call from the budget; false once calls or time are spent.</summary>
    public bool TryTake() =>
        _time.GetElapsedTime(_started) < timeBox && Interlocked.Increment(ref _calls) <= maxCalls;

    /// <summary>Wraps <paramref name="tools"/> so each call draws on this budget.</summary>
    public IList<AITool> Wrap(IEnumerable<AITool> tools) =>
        [.. tools.Select(t => t is AIFunction f ? new BudgetedFunction(f, this) : t)];

    private sealed class BudgetedFunction(AIFunction inner, ResearchToolBudget budget) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            budget.TryTake()
                ? base.InvokeCoreAsync(arguments, cancellationToken)
                : ValueTask.FromResult<object?>(ExhaustedNote);
    }
}
