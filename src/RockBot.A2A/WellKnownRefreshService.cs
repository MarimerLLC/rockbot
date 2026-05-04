using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RockBot.A2A;

/// <summary>
/// Periodically re-fetches every well-known peer's <c>/.well-known/agent-card.json</c>
/// so changes to a peer's skills/metadata become visible without restarting. When a
/// peer's skill set changes, the cached <see cref="AgentDirectoryEntry.LlmSummary"/>
/// is regenerated.
/// </summary>
internal sealed class WellKnownRefreshService(
    AgentDirectory directory,
    AgentCardSummarizer summarizer,
    A2AOptions options,
    ILogger<WellKnownRefreshService> logger) : IHostedService
{
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.WellKnownRefreshInterval <= TimeSpan.Zero)
        {
            logger.LogInformation("Periodic well-known refresh disabled (interval is zero or negative)");
            return Task.CompletedTask;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RefreshLoopAsync(_loopCts.Token));
        logger.LogInformation(
            "Started periodic well-known refresh loop (interval={Interval})",
            options.WellKnownRefreshInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask; }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }
        _loopCts?.Dispose();
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(options.WellKnownRefreshInterval, ct);
                await RunRefreshPassAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Well-known refresh loop terminated unexpectedly");
        }
    }

    private async Task RunRefreshPassAsync(CancellationToken ct)
    {
        try
        {
            var results = await directory.RefreshAllWellKnownAsync(ct);
            foreach (var result in results)
            {
                if (!result.SkillsChanged) continue;

                var card = directory.GetAgent(result.AgentName);
                if (card is null) continue;

                var summary = await summarizer.SummarizeAsync(card, ct);
                directory.SetSummary(result.AgentName, summary);
                logger.LogInformation(
                    "Regenerated LLM summary for '{AgentName}' after skill changes",
                    result.AgentName);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Well-known refresh pass failed");
        }
    }
}
