using Microsoft.Extensions.Logging;
using RockBot.A2A;

namespace RockBot.ResearchAgent;

/// <summary>
/// The A2A path into research, kept while callers move to the MCP <c>research</c> tool
/// (<c>design/research-agent-mcp.md</c>, Phases 3–5). Takes the request's text as the question
/// and runs it through the same <see cref="IResearchRunner"/> as the MCP tool, publishing
/// <c>Working</c> status updates as it goes. A2A callers have no clarifying-question round trip,
/// so this path never scopes or asks.
/// </summary>
internal sealed class ResearchAgentTaskHandler(
    IResearchRunner runner,
    ILogger<ResearchAgentTaskHandler> logger) : IAgentTaskHandler
{
    public async Task<AgentTaskResult> HandleTaskAsync(AgentTaskRequest request, AgentTaskContext context)
    {
        var ct = context.MessageContext.CancellationToken;

        logger.LogInformation("Handling research task {TaskId} (skill={Skill})", request.TaskId, request.Skill);

        try
        {
            // Notify caller we're actively working
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

            logger.LogInformation("Research question for task {TaskId}: {Question}", request.TaskId, question);

            async Task OnProgress(string progressMsg, CancellationToken progressCt) =>
                await context.PublishStatus(new AgentTaskStatusUpdate
                {
                    TaskId = request.TaskId,
                    ContextId = request.ContextId,
                    State = AgentTaskState.Working,
                    Message = new AgentMessage
                    {
                        Role = "agent",
                        Parts = [new AgentMessagePart { Kind = "text", Text = progressMsg }]
                    }
                }, progressCt);

            var outcome = await runner.RunAsync(new ResearchRequest(question), request.TaskId, OnProgress, ct);

            return new AgentTaskResult
            {
                TaskId = request.TaskId,
                ContextId = request.ContextId,
                State = AgentTaskState.Completed,
                Message = new AgentMessage
                {
                    Role = "agent",
                    Parts = [new AgentMessagePart { Kind = "text", Text = outcome.Answer }]
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Research task {TaskId} failed", request.TaskId);
            throw;
        }
    }
}
