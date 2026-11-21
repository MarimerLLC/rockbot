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
        _logger = loggerFactory.CreateLogger<SavedWispRunner>();
    }

    internal SavedWispRunner(SpawnWispsExecutor executor, ILogger<SavedWispRunner> logger)
    {
        _executor = executor;
        _logger = logger;
    }

    public async Task<SavedWispRun> RunAsync(string definitionJson, string? sessionId, CancellationToken ct)
    {
        var definition = SpawnWispsExecutor.ParseDefinition(definitionJson, out var error);
        if (definition is null)
            return new SavedWispRun(false, string.Empty, null, null, error);

        try
        {
            var result = await _executor.ExecuteSavedAsync(definition, sessionId, ct);
            var last = result.StepResults.LastOrDefault(s => s.IsSuccess && !s.WasSkipped && s.Content is not null);
            return new SavedWispRun(
                result.IsSuccess,
                result.WispId,
                last?.Content,
                last is null ? null : $"wisp/{result.WispId}/{last.StepId}/output",
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
            return new SavedWispRun(false, string.Empty, null, null, ex.Message);
        }
    }

    public SavedWispCheck Inspect(string definitionJson)
    {
        var definition = SpawnWispsExecutor.ParseDefinition(definitionJson, out var error);
        if (definition is null)
            return new SavedWispCheck(false, false, error);
        return InspectDefinition(definition) with { ShapeHash = SpawnWispsExecutor.ComputeShapeHash(definition) };
    }

    /// <summary>Whether every step of <paramref name="definition"/> is a direct call that only reads.</summary>
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
                    if (ToolSideEffects.IsSideEffecting(step.Tool))
                        return new SavedWispCheck(true, false, $"step '{step.Id}' calls '{step.Tool}', which writes");
                    break;
                case GatewayType.Web:
                case GatewayType.Script:
                    // Web reads; scripts run in the sandbox and change nothing outside it.
                    break;
                default:
                    return new SavedWispCheck(true, false,
                        $"step '{step.Id}' uses the {step.Gateway?.ToString() ?? "unknown"} gateway");
            }
        }
        return new SavedWispCheck(true, true, null);
    }
}
