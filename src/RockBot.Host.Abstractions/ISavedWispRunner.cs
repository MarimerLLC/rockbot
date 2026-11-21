namespace RockBot.Host;

/// <summary>
/// Runs a saved wisp (a skill's wisp resource) outside any LLM turn, for a scheduled task's gate
/// (#848). Implemented by the wisp subsystem; absent when wisps are not registered.
/// </summary>
public interface ISavedWispRunner
{
    /// <summary>
    /// Runs the wisp in <paramref name="definitionJson"/> on behalf of <paramref name="sessionId"/>.
    /// The run is logged like any other wisp run, so it counts toward promotion. Never throws for a
    /// bad definition or a failed step; the result says what happened.
    /// </summary>
    Task<SavedWispRun> RunAsync(string definitionJson, string? sessionId, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="definitionJson"/> is a valid wisp whose every step is known to only
    /// read: a direct MCP call whose tool is named with a read verb (and no write verb), or a web
    /// search or fetch. LLM, agent and script steps are not. Only such a wisp may be switched on as
    /// a gate without a person asking for it.
    /// </summary>
    SavedWispCheck Inspect(string definitionJson);
}

/// <summary>What a saved wisp's run produced.</summary>
/// <param name="Succeeded">True when every step succeeded (or its failure was handled).</param>
/// <param name="WispId">The run's wisp id.</param>
/// <param name="Outputs">Every step that produced output, in step order; empty when none did.</param>
/// <param name="Error">Why the run failed, when it did.</param>
public sealed record SavedWispRun(bool Succeeded, string WispId, IReadOnlyList<SavedWispStepOutput> Outputs, string? Error);

/// <summary>One step's output from a saved wisp's run.</summary>
/// <param name="StepId">The step's id.</param>
/// <param name="Content">What the step returned.</param>
/// <param name="Key">The working-memory key holding it, <c>wisp/{id}/{step}/output</c>, for a worker's <c>input_keys</c>.</param>
public sealed record SavedWispStepOutput(string StepId, string Content, string Key);

/// <summary>What <see cref="ISavedWispRunner.Inspect"/> found.</summary>
/// <param name="Valid">True when the definition parses and has steps.</param>
/// <param name="ReadOnly">True when every step is a direct call that only reads.</param>
/// <param name="Reason">Why it is invalid or not read-only.</param>
/// <param name="ShapeHash">
/// The wisp's shape hash, the one its runs are logged under, so a resource saved from it can be
/// matched to its runs; null when it is invalid.
/// </param>
public sealed record SavedWispCheck(bool Valid, bool ReadOnly, string? Reason, string? ShapeHash = null);
