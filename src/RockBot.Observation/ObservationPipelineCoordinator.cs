using Microsoft.Extensions.Logging;

namespace RockBot.Observation;

/// <summary>
/// Default <see cref="IObservationPipelineCoordinator"/>. Runs targets
/// sequentially: each target applies its own filter, runs phase 1, then
/// phase 2. Per-target exceptions are caught and logged so a failing
/// target does not block its peers.
/// </summary>
internal sealed class ObservationPipelineCoordinator(
    IEnumerable<ObservationTarget> targets,
    IObservationExtractionPhase extractionPhase,
    IObservationEvaluationPhase evaluationPhase,
    ILogger<ObservationPipelineCoordinator> logger) : IObservationPipelineCoordinator
{
    private readonly List<ObservationTarget> _targets = targets.ToList();

    public async Task<IReadOnlyList<ObservationTargetRunResult>> RunAllAsync(
        IReadOnlyList<TranscriptTurn> transcripts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transcripts);

        if (_targets.Count == 0)
        {
            logger.LogInformation("Observation: no targets registered; pipeline is a no-op");
            return [];
        }

        var results = new List<ObservationTargetRunResult>(_targets.Count);

        foreach (var target in _targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var filtered = target.Filter.Filter(transcripts).ToList();

                var extractionResult = await extractionPhase
                    .ExecuteAsync(target, filtered, cancellationToken)
                    .ConfigureAwait(false);

                var evaluationResult = await evaluationPhase
                    .ExecuteAsync(target, cancellationToken)
                    .ConfigureAwait(false);

                results.Add(new ObservationTargetRunResult(
                    target.Name, extractionResult, evaluationResult, Failure: null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Observation: pipeline failed for target {Target}; other targets continue",
                    target.Name);
                results.Add(new ObservationTargetRunResult(
                    target.Name, ExtractionResult: null, EvaluationResult: null, Failure: ex));
            }
        }

        return results;
    }
}
