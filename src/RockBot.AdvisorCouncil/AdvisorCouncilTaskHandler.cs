using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RockBot.A2A;
using RockBot.AdvisorCouncil.Council;
using RockBot.AdvisorCouncil.Schema;

namespace RockBot.AdvisorCouncil;

/// <summary>
/// A2A entry point for the AdvisorCouncil. Extracts the question from the inbound
/// request, runs <see cref="CouncilOrchestrator"/>, and returns a two-part
/// <see cref="AgentTaskResult"/> (text = synthesis prose; data = full JSON).
/// </summary>
internal sealed class AdvisorCouncilTaskHandler(
    CouncilOrchestrator orchestrator,
    IOptions<CouncilOptions> options,
    EphemeralShutdownCoordinator shutdown,
    ILogger<AdvisorCouncilTaskHandler> logger) : IAgentTaskHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;
        logger.LogInformation("Handling council task {TaskId} (skill={Skill})", request.TaskId, request.Skill);

        try
        {
            await context.PublishStatus(new AgentTaskStatusUpdate
            {
                TaskId = request.TaskId,
                ContextId = request.ContextId,
                State = AgentTaskState.Working
            }, ct);

            var question = request.Message.Parts
                .Where(p => p.Kind == "text")
                .Select(p => p.Text)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                ?? "(no question provided)";

            logger.LogInformation("Council question for task {TaskId}: {Question}",
                request.TaskId, question.Length > 300 ? question[..300] + "…" : question);

            var overallTimeout = TimeSpan.FromSeconds(Math.Max(30, options.Value.OverallTimeoutSeconds));
            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(overallTimeout);

            CouncilResponse response;
            try
            {
                response = await orchestrator.RunAsync(question, request.TaskId, overallCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Council task {TaskId} hit overall timeout ({Sec}s)",
                    request.TaskId, overallTimeout.TotalSeconds);
                return new AgentTaskResult
                {
                    TaskId = request.TaskId,
                    ContextId = request.ContextId,
                    State = AgentTaskState.Failed,
                    Message = new AgentMessage
                    {
                        Role = "agent",
                        Parts = [new AgentMessagePart { Kind = "text", Text = "Council deliberation timed out." }]
                    }
                };
            }

            var json = JsonSerializer.Serialize(response, JsonOpts);
            var textPart = BuildTextPart(response);

            return new AgentTaskResult
            {
                TaskId = request.TaskId,
                ContextId = request.ContextId,
                State = AgentTaskState.Completed,
                Message = new AgentMessage
                {
                    Role = "agent",
                    Parts =
                    [
                        new AgentMessagePart { Kind = "text", Text = textPart },
                        new AgentMessagePart { Kind = "data", Data = json, MimeType = "application/json" }
                    ]
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Council task {TaskId} failed", request.TaskId);
            throw;
        }
        finally
        {
            shutdown.NotifyTaskComplete();
        }
    }

    /// <summary>
    /// Prepends a coverage banner to the synthesis prose when one or more selected personas
    /// did not contribute (timed out or failed). The caller would otherwise have no way of
    /// knowing the recommendation is missing perspectives.
    /// </summary>
    private static string BuildTextPart(CouncilResponse response)
    {
        var missing = response.Personas
            .Where(p => p.Status != PersonaStatus.Ok)
            .ToList();

        if (missing.Count == 0)
            return response.Synthesis;

        var contributed = response.Personas.Count - missing.Count;
        var missingDetail = string.Join(", ",
            missing.Select(p => $"{p.Id} ({p.Status})"));

        var banner =
            $"> **Council coverage:** {contributed} of {response.Personas.Count} personas contributed. " +
            $"Missing perspectives: {missingDetail}. The integrated recommendation below does not reflect those viewpoints.";

        return banner + "\n\n---\n\n" + response.Synthesis;
    }
}
