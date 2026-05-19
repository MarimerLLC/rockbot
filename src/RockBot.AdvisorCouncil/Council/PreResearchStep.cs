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
    ResearchAgentInvoker invoker,
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
            var enrichedQuestion = BuildPersonaAwareQuestion(question, selectedPersonas);
            var findings = await invoker.InvokeAsync(enrichedQuestion, ct);
            if (string.IsNullOrWhiteSpace(findings))
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

    private static string BuildPersonaAwareQuestion(string question, IReadOnlyList<Persona> personas)
    {
        if (personas.Count == 0)
            return question;

        var sb = new StringBuilder();
        sb.Append("Investigate the following question. The council will examine it through these lenses, ")
          .AppendLine("so gather facts relevant to each lens, not just the question in isolation:");
        foreach (var p in personas)
        {
            sb.Append("- ").Append(p.Id);
            if (!string.IsNullOrWhiteSpace(p.Description))
                sb.Append(" — ").Append(p.Description);
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.Append("Question: ").Append(question);
        return sb.ToString();
    }
}
