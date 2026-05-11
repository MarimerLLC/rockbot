using Microsoft.Extensions.Logging;
using RockBot.AdvisorCouncil.Tools;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Conditional stage that runs a single research call up-front and injects the findings
/// into every persona branch. Skipped unless the selector returns <c>pre_research=true</c>.
/// </summary>
internal sealed class PreResearchStep(
    ResearchAgentInvoker invoker,
    ILogger<PreResearchStep> logger)
{
    public async Task<string?> RunAsync(string question, CancellationToken ct)
    {
        try
        {
            var findings = await invoker.InvokeAsync(question, ct);
            logger.LogInformation("Pre-research returned {Len} chars", findings.Length);
            return findings;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pre-research call failed; continuing without findings");
            return null;
        }
    }
}
