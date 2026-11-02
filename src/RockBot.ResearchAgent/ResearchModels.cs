namespace RockBot.ResearchAgent;

/// <summary>One research run's input.</summary>
/// <param name="Question">What to research.</param>
/// <param name="Context">Background the caller already has: who is asking, why, what is known.</param>
/// <param name="Constraints">Scope limits: time range, region, sources to prefer or avoid, depth.</param>
/// <param name="Interpretation">
/// The reading of an ambiguous question the research should follow, chosen by the caller or
/// assumed by the scoping pass. Null when the question was not ambiguous.
/// </param>
internal sealed record ResearchRequest(
    string Question,
    string? Context = null,
    string? Constraints = null,
    string? Interpretation = null);

/// <summary>A page the research loop read.</summary>
internal sealed record ResearchSource(string Url, string? Title = null);

/// <summary>One research run's output.</summary>
internal sealed record ResearchOutcome(string Answer, IReadOnlyList<ResearchSource> Sources);

/// <summary>Runs one research question to a synthesised answer.</summary>
internal interface IResearchRunner
{
    /// <param name="request">The question and its scope.</param>
    /// <param name="runId">Names the run's working-memory namespace (<c>research/{runId}</c>).</param>
    /// <param name="onProgress">Optional progress callback, for callers that report status.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<ResearchOutcome> RunAsync(
        ResearchRequest request,
        string runId,
        Func<string, CancellationToken, Task>? onProgress,
        CancellationToken cancellationToken);
}
