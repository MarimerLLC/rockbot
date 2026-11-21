using Microsoft.Extensions.Logging;
using RockBot.Host;
using RockBot.Tools;

namespace RockBot.Wisp;

/// <summary>
/// Runs a saved wisp for a scheduled task's gate (#848). It goes through the same executor
/// path as <c>spawn_wisps</c>, so the run is logged, counts toward promotion, and is refused by
/// the dispatch circuit breaker the same way.
/// </summary>
internal sealed class SavedWispRunner : ISavedWispRunner
{
    private readonly SpawnWispsExecutor _executor;
    private readonly IMcpToolDirectory? _mcpToolDirectory;
    private readonly ILogger<SavedWispRunner> _logger;

    public SavedWispRunner(
        WispExecutor wispExecutor,
        IWorkingMemory workingMemory,
        WispOptions options,
        ILoggerFactory loggerFactory,
        WispDispatchCircuitBreaker circuitBreaker,
        IWispExecutionLog? executionLog = null,
        IFeedbackStore? feedbackStore = null,
        ISkillStore? skillStore = null,
        ISkillUsageStore? skillUsageStore = null,
        IMcpToolDirectory? mcpToolDirectory = null)
    {
        _executor = new SpawnWispsExecutor(wispExecutor, executionLog, feedbackStore, workingMemory, options,
            loggerFactory.CreateLogger<SpawnWispsExecutor>(), circuitBreaker, skillStore, skillUsageStore,
            mcpToolDirectory);
        _mcpToolDirectory = mcpToolDirectory;
        _logger = loggerFactory.CreateLogger<SavedWispRunner>();
    }

    internal SavedWispRunner(SpawnWispsExecutor executor, ILogger<SavedWispRunner> logger,
        IMcpToolDirectory? mcpToolDirectory = null)
    {
        _executor = executor;
        _mcpToolDirectory = mcpToolDirectory;
        _logger = logger;
    }

    public async Task<SavedWispRun> RunAsync(string definitionJson, string? sessionId, CancellationToken ct)
    {
        var definition = SpawnWispsExecutor.ParseDefinition(definitionJson, out var error);
        if (definition is null)
            return new SavedWispRun(false, string.Empty, [], error);

        try
        {
            var result = await _executor.ExecuteSavedAsync(definition, sessionId, ct);
            // Every step that produced output: a gate is judged on all of them, not just the last.
            var outputs = result.StepResults
                .Where(s => s.IsSuccess && !s.WasSkipped && s.Content is not null)
                .Select(s => new SavedWispStepOutput(s.StepId, s.Content!, $"wisp/{result.WispId}/{s.StepId}/output"))
                .ToList();
            return new SavedWispRun(
                result.IsSuccess,
                result.WispId,
                outputs,
                result.IsSuccess ? null : result.FailedStep?.Error?.Message ?? "The wisp failed");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saved wisp '{Description}' threw for session {Session}",
                definition.Description, sessionId);
            return new SavedWispRun(false, string.Empty, [], ex.Message);
        }
    }

    public SavedWispCheck Inspect(string definitionJson)
    {
        var definition = SpawnWispsExecutor.ParseDefinition(definitionJson, out var error);
        if (definition is null)
            return new SavedWispCheck(false, false, error);

        // The same canonical form the executor logs runs under, so the shape hash matches them.
        definition = McpStepResolver.CanonicalizeShorthand(definition, _mcpToolDirectory);
        return InspectDefinition(definition) with { ShapeHash = SpawnWispsExecutor.ComputeShapeHash(definition) };
    }

    /// <summary>
    /// Whether every step of <paramref name="definition"/> is a direct call known to only read. This
    /// decides whether the dream may switch a gate on with nobody reviewing it, so it is an allow-list:
    /// an MCP tool must be named with a read verb first (<c>get_</c>, <c>list_</c>, <c>search_</c>, …)
    /// and no write verb; a web step fetches; a script step is refused, since nothing here can tell
    /// whether its code calls out.
    /// </summary>
    internal static SavedWispCheck InspectDefinition(WispDefinition definition)
    {
        foreach (var step in definition.Steps)
        {
            if (step.Mode != StepMode.Direct)
                return new SavedWispCheck(true, false, $"step '{step.Id}' is an LLM step");

            switch (step.Gateway)
            {
                case GatewayType.Mcp:
                    if (string.IsNullOrWhiteSpace(step.Tool))
                        return new SavedWispCheck(true, false, $"step '{step.Id}' names no tool");
                    if (!ToolSideEffects.IsKnownReadOnlyName(step.Tool))
                        return new SavedWispCheck(true, false,
                            $"step '{step.Id}' calls '{step.Tool}', which is not named as a read");
                    break;
                case GatewayType.Web:
                    break;
                default:
                    return new SavedWispCheck(true, false,
                        $"step '{step.Id}' uses the {step.Gateway?.ToString() ?? "unknown"} gateway");
            }
        }
        return new SavedWispCheck(true, true, null);
    }
}
