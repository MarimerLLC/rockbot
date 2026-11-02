using System.Text;
using Microsoft.Extensions.Logging;
using RockBot.AdvisorCouncil.Personas;
using RockBot.AdvisorCouncil.Tools;
using RockBot.Host;

namespace RockBot.AdvisorCouncil.Council;

/// <summary>
/// Conditional stage that runs a single research call up-front and writes the findings to
/// working memory at <c>council/{taskId}/shared</c>, where each persona can read them.
/// Skipped unless the selector returns <c>pre_research=true</c>.
///
/// The research question is enriched with the selected council roster so the research
/// agent gathers facts relevant to each persona's lens, not just the question in isolation.
/// </summary>
internal sealed class PreResearchStep(
    IResearchClient research,
    IWorkingMemory workingMemory,
    ILogger<PreResearchStep> logger)
{
    /// <summary>
    /// Runs persona-aware pre-research and stores the findings in working memory.
    /// Returns true if findings were written (consumed by orchestrator metadata).
    /// </summary>
    public async Task<bool> RunAsync(
        string question,
        IReadOnlyList<Persona> selectedPersonas,
        string taskId,
        CancellationToken ct)
    {
        try
        {
            var findings = await research.InvokeAsync(question, BuildLensContext(selectedPersonas), ct);
            if (string.IsNullOrWhiteSpace(findings) || findings.StartsWith("(research", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Pre-research returned empty findings; not writing to WM");
                return false;
            }

            await workingMemory.SetAsync(
                key: $"council/{taskId}/shared",
                value: findings,
                ttl: TimeSpan.FromMinutes(30),
                category: "council/research",
                tags: ["shared", taskId]);

            logger.LogInformation("Pre-research wrote {Len} chars to council/{TaskId}/shared", findings.Length, taskId);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pre-research call failed; continuing without findings");
            return false;
        }
    }

    /// <summary>
    /// The research call's <c>context</c>: who is asking and through which lenses, so research
    /// gathers facts for each lens and can settle its own clarifying question from it.
    /// </summary>
    internal static string BuildLensContext(IReadOnlyList<Persona> personas)
    {
        var sb = new StringBuilder();
        sb.Append("Pre-research for an advisor council that will examine this question.");
        if (personas.Count > 0)
        {
            sb.AppendLine(" It will look at it through these lenses, so gather facts relevant to each lens, " +
                          "not just the question in isolation:");
            foreach (var p in personas)
            {
                sb.Append("- ").Append(p.Id);
                if (!string.IsNullOrWhiteSpace(p.Description))
                    sb.Append(" — ").Append(p.Description);
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }
}
